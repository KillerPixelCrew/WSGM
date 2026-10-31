// Read module fingerprints from emitted JavaScript without evaluating it. Use the same parser as
// the installed-bundle reader, so escaped strings and scoped constants mean what JavaScript says.
import * as meriyah from "prettier/plugins/meriyah";

const parser = (meriyah.parsers ?? meriyah.default.parsers).meriyah;
const resolverMethods = new Set([
  "count",
  "findUnique",
  "resolve",
  "exported",
  "uniqueFactory",
  "optionalSteamExport",
]);

export const readFingerprints = (source) => {
  const candidates = [];
  const calls = [];
  const visit = (node, parent) => {
    if (!node || typeof node !== "object") return;
    if (Array.isArray(node)) {
      node.forEach((child) => visit(child, parent));
      return;
    }
    const isFunction = /^(?:FunctionDeclaration|FunctionExpression|ArrowFunctionExpression)$/u.test(
      node.type,
    );
    const scope =
      node.type === "Program" || node.type === "BlockStatement" || isFunction
        ? { parent, bindings: new Map() }
        : parent;
    if (node.type === "FunctionDeclaration" && node.id && parent) {
      parent.bindings.set(node.id.name, { value: node, scope: parent });
    }
    if (isFunction) {
      node.params.forEach((parameter, index) => {
        if (parameter.type === "Identifier") {
          scope.bindings.set(parameter.name, {
            value: null,
            scope,
            parameter: { owner: node, index },
          });
        }
      });
    }
    if (node.type === "VariableDeclarator" && node.id.type === "Identifier") {
      scope.bindings.set(node.id.name, { value: node.init, scope });
    }
    if (node.type === "CallExpression") {
      const name =
        node.callee.type === "Identifier"
          ? node.callee.name
          : node.callee.type === "MemberExpression" && !node.callee.computed
            ? node.callee.property.name
            : null;
      const argument = node.arguments[name === "optionalSteamExport" ? 1 : 0];
      const isPromise =
        node.callee.type === "MemberExpression" &&
        node.callee.object.type === "Identifier" &&
        node.callee.object.name === "Promise";
      if (
        resolverMethods.has(name) &&
        !isPromise &&
        (argument?.type === "ArrayExpression" ||
          (argument?.type === "Identifier" && /Tokens$/u.test(argument.name)))
      ) {
        candidates.push({ node: argument, scope });
      }
      if (node.callee.type === "Identifier") calls.push({ node, scope });
    }
    for (const [name, child] of Object.entries(node)) {
      if (name !== "loc" && name !== "range") visit(child, scope);
    }
  };
  visit(parser.parse(source, {}), null);

  const bindingOf = (name, scope) => {
    let owner = scope;
    while (owner && !owner.bindings.has(name)) owner = owner.parent;
    return owner?.bindings.get(name);
  };

  const valueOf = (node, scope, seen = new Set()) => {
    if (!node) return undefined;
    if (node.type === "Literal") return node.value;
    if (node.type === "Identifier") {
      const binding = bindingOf(node.name, scope);
      if (!binding || seen.has(binding)) return undefined;
      return valueOf(binding.value, binding.scope, new Set([...seen, binding]));
    }
    if (node.type === "ArrayExpression") {
      const result = [];
      for (const element of node.elements) {
        if (element?.type === "SpreadElement") {
          const spread = valueOf(element.argument, scope, seen);
          if (!Array.isArray(spread)) return undefined;
          result.push(...spread);
        } else {
          const value = valueOf(element, scope, seen);
          if (typeof value !== "string") return undefined;
          result.push(value);
        }
      }
      return result;
    }
    if (node.type === "MemberExpression" && node.computed) {
      const values = valueOf(node.object, scope, seen);
      const index = valueOf(node.property, scope, seen);
      if (Array.isArray(values) && Number.isInteger(index)) return values[index];
    }
    return undefined;
  };

  // A helper that hands one of its parameters, whole, to a resolver forwards the fingerprint its
  // callers pass: `exportOf(runtime, StoreTokens, isStore)` around `runtime.exported([...tokens], ...)`.
  // Its fingerprints are read at its call sites, and a forwarder nothing calls with a readable
  // fingerprint fails like any other unread query.
  const forwardedParameter = (node, scope) => {
    const identifier =
      node.type === "Identifier"
        ? node
        : node.type === "ArrayExpression" &&
            node.elements.length === 1 &&
            node.elements[0]?.type === "SpreadElement" &&
            node.elements[0].argument.type === "Identifier"
          ? node.elements[0].argument
          : null;
    return identifier ? (bindingOf(identifier.name, scope)?.parameter ?? null) : null;
  };

  const results = [];
  const forwarders = new Map();
  const pending = [...candidates];
  while (pending.length) {
    const { node, scope } = pending.shift();
    const forwarded = node && forwardedParameter(node, scope);
    if (forwarded) {
      if (forwarders.has(forwarded.owner)) continue;
      const sites = calls.filter(
        (call) => bindingOf(call.node.callee.name, call.scope)?.value === forwarded.owner,
      );
      forwarders.set(forwarded.owner, sites.length);
      if (sites.length === 0) results.push({ node, tokens: undefined });
      for (const call of sites) {
        pending.push({
          node: call.node.arguments[forwarded.index] ?? call.node,
          scope: call.scope,
        });
      }
      continue;
    }
    results.push({ node, tokens: valueOf(node, scope) });
  }

  return results
    .sort((a, b) => parser.locStart(a.node) - parser.locStart(b.node))
    .map(({ node, tokens }) => {
      const offset = parser.locStart(node);
      if (
        !Array.isArray(tokens) ||
        tokens.length === 0 ||
        !tokens.every((token) => typeof token === "string" && token.length > 0)
      ) {
        const line = source.slice(0, offset).split("\n").length;
        throw new Error(`Cannot read the emitted module fingerprint at line ${line}.`);
      }
      return { tokens, offset };
    });
};
