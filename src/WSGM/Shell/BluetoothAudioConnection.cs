using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WindowsDeviceControl;

namespace WSGM.Shell;

/// <summary>Confirms a single audio connection request through later endpoint observations.</summary>
internal sealed class BluetoothAudioConnection(
    Action<string, bool> write,
    Func<IReadOnlyList<CoreAudio.BluetoothAudioContainer>> read,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal async Task<bool> ApplyAsync(string container, bool connected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => write(container, connected), cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containers = await Task.Run(read, cancellationToken).ConfigureAwait(false);
            var match = containers.FirstOrDefault(item => string.Equals(
                item.Container.Trim('{', '}'), container.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Container) && match.Active == connected) { return true; }
            await (delay ?? Task.Delay)(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }
}
