using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WSGM.Browser;

namespace WSGM.BrowserHost;

internal sealed partial class BrowserWindow
{
    private const string InputBinding = "wsgmTextInput";
    private readonly string _inputWorld = "WSGM.Input." + Guid.NewGuid().ToString("N");
    private readonly HashSet<int> _inputContexts = [];
    private int _textInputContext;
    private CoreWebView2DevToolsProtocolEventReceiver? _inputCreated;
    private CoreWebView2DevToolsProtocolEventReceiver? _inputDestroyed;
    private CoreWebView2DevToolsProtocolEventReceiver? _inputCleared;
    private CoreWebView2DevToolsProtocolEventReceiver? _inputMessages;

    private async Task InitializeTextInputAsync(CoreWebView2 web)
    {
        // The binding exists only in our isolated world. Website scripts have no native message endpoint.
        _inputCreated = web.GetDevToolsProtocolEventReceiver("Runtime.executionContextCreated");
        _inputDestroyed = web.GetDevToolsProtocolEventReceiver("Runtime.executionContextDestroyed");
        _inputCleared = web.GetDevToolsProtocolEventReceiver("Runtime.executionContextsCleared");
        _inputMessages = web.GetDevToolsProtocolEventReceiver("Runtime.bindingCalled");
        _inputCreated.DevToolsProtocolEventReceived += InputContextCreated;
        _inputDestroyed.DevToolsProtocolEventReceived += InputContextDestroyed;
        _inputCleared.DevToolsProtocolEventReceived += InputContextsCleared;
        _inputMessages.DevToolsProtocolEventReceived += TextInputMessage;
        await web.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await web.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await web.CallDevToolsProtocolMethodAsync("Runtime.addBinding",
            JsonSerializer.Serialize(new { name = InputBinding, executionContextName = _inputWorld }));
        await web.CallDevToolsProtocolMethodAsync("Page.addScriptToEvaluateOnNewDocument",
            JsonSerializer.Serialize(new { source = TextInputScript, worldName = _inputWorld }));
    }

    private void InputContextCreated(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        using var json = JsonDocument.Parse(e.ParameterObjectAsJson);
        var context = json.RootElement.GetProperty("context");
        if (context.GetProperty("name").GetString() == _inputWorld
            && context.TryGetProperty("auxData", out var auxiliary)
            && auxiliary.TryGetProperty("isDefault", out var isDefault) && !isDefault.GetBoolean())
        {
            _inputContexts.Add(context.GetProperty("id").GetInt32());
        }
    }

    private void InputContextDestroyed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        using var json = JsonDocument.Parse(e.ParameterObjectAsJson);
        _inputContexts.Remove(json.RootElement.GetProperty("executionContextId").GetInt32());
    }

    private void InputContextsCleared(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        _inputContexts.Clear();
    }

    private void TextInputMessage(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (_closing || !_visible)
        {
            return;
        }

        using var json = JsonDocument.Parse(e.ParameterObjectAsJson);
        var message = json.RootElement;
        if (message.GetProperty("name").GetString() != InputBinding
            || !_inputContexts.Contains(message.GetProperty("executionContextId").GetInt32()))
        {
            return;
        }

        var payload = message.GetProperty("payload").GetString();
        if (payload is null || payload.Length > BrowserWire.MaximumPacketBytes)
        {
            return;
        }

        using var input = JsonDocument.Parse(payload);
        var state = input.RootElement;
        if (state.GetProperty("editable").GetBoolean())
        {
            _textInputContext = message.GetProperty("executionContextId").GetInt32();
        }

        var text = state.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
        if (text.Length > 4096)
        {
            return;
        }

        Send(new BrowserPacket
        {
            Kind = state.GetProperty("request").GetBoolean() ? "keyboard" : "field",
            Visible = state.GetProperty("editable").GetBoolean(),
            Sensitive = state.GetProperty("sensitive").GetBoolean(),
            Multiline = state.GetProperty("multiline").GetBoolean(),
            Text = text,
            Replace = state.TryGetProperty("replace", out var replace) && replace.GetBoolean(),
            SelectionStart = state.TryGetProperty("start", out var start) ? start.GetInt32() : 0,
            SelectionEnd = state.TryGetProperty("end", out var end) ? end.GetInt32() : 0,
            NavigationId = _navigationId
        });
    }

    private async Task<bool> InvokeTextInputAsync(CoreWebView2 web, string expression)
    {
        if (!_inputContexts.Contains(_textInputContext))
        {
            return false;
        }

        var response = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression, contextId = _textInputContext, returnByValue = true }));
        using var json = JsonDocument.Parse(response);
        return json.RootElement.GetProperty("result").TryGetProperty("value", out var value)
               && value.ValueKind == JsonValueKind.True;
    }

    private void DetachTextInputEvents()
    {
        if (_inputCreated is not null)
        {
            _inputCreated.DevToolsProtocolEventReceived -= InputContextCreated;
            _inputDestroyed!.DevToolsProtocolEventReceived -= InputContextDestroyed;
            _inputCleared!.DevToolsProtocolEventReceived -= InputContextsCleared;
            _inputMessages!.DevToolsProtocolEventReceived -= TextInputMessage;
        }

        _inputContexts.Clear();
    }

    private const string TextInputScript = """
        (() => {
          const fields = 'input,textarea,[contenteditable]';
          let selected = null, editing = null;
          const editable = node => node instanceof HTMLElement && !node.disabled && !node.readOnly &&
            (node instanceof HTMLTextAreaElement || node.isContentEditable ||
             (node instanceof HTMLInputElement && /^(text|search|url|tel|email|password|number)$/.test(node.type)));
          const prepare = node => {
            if (editable(node) && node.getAttribute('inputmode') !== 'none') node.setAttribute('inputmode', 'none');
          };
          const scan = root => {
            prepare(root);
            root.querySelectorAll?.(fields).forEach(prepare);
          };
          const active = () => {
            let node = document.activeElement;
            while (node?.shadowRoot?.activeElement) node = node.shadowRoot.activeElement;
            return editable(node) ? node : null;
          };
          const report = (node, request, extra = {}) => wsgmTextInput(JSON.stringify({
            request, editable: !!node, sensitive: node?.type === 'password',
            multiline: node instanceof HTMLTextAreaElement || !!node?.isContentEditable, ...extra
          }));
          const request = node => {
            if (!editable(node) || !node.isConnected) return false;
            const replace = !node.isContentEditable && node.value.length <= 4096;
            editing = {node, start: node.selectionStart, end: node.selectionEnd,
              range: node.isContentEditable && getSelection()?.rangeCount ? getSelection().getRangeAt(0).cloneRange() : null};
            report(node, true, {replace, text: replace ? node.value : '',
              start: replace ? node.selectionStart ?? node.value.length : 0,
              end: replace ? node.selectionEnd ?? node.value.length : 0});
            return true;
          };
          globalThis.wsgmRequestTextInput = () => request(active() || selected);
          globalThis.wsgmRestoreTextInput = () => {
            const saved = editing || (selected ? {node: selected} : null), node = saved?.node;
            if (!editable(node) || !node.isConnected) return false;
            node.focus({preventScroll: true});
            if (saved.range) {const range=getSelection();range.removeAllRanges();range.addRange(saved.range);}
            else if (typeof saved.start === 'number') {
              try {node.setSelectionRange(saved.start, saved.end);} catch {}
            }
            editing = null;
            return true;
          };
          const target = event => event.composedPath().find(editable);
          scan(document);
          new MutationObserver(changes => {
            for (const change of changes) {
              if (change.type === 'attributes') prepare(change.target);
              else for (const node of change.addedNodes) scan(node);
            }
          }).observe(document, {subtree: true, childList: true, attributes: true,
            attributeFilter: ['inputmode', 'contenteditable', 'type', 'readonly', 'disabled']});
          document.addEventListener('pointerdown', event => prepare(target(event)), true);
          document.addEventListener('focusin', event => {
            const node = target(event);
            prepare(node);
            if (node) selected = node;
            report(node || null, false);
          }, true);
          document.addEventListener('click', event => {
            const node = target(event);
            if (event.isTrusted && node && (event.pointerType === 'touch' || event.pointerType === 'pen')) {
              setTimeout(() => { if (active() === node) request(node); }, 0);
            }
          }, true);
          document.addEventListener('focusout', () => setTimeout(() => report(active(), false), 0), true);
        })();
        """;
}
