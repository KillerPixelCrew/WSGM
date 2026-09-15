using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace WSGM.AllyXLab;

internal sealed class Session
{
    internal string DirectoryPath { get; }
    internal string RecoveryPath { get; }
    private int _sequence;
    internal Process? Process { get; private set; }
    internal List<Result> Results { get; } = [];
    internal Session()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WSGM.AllyXLab");
        for (DirectoryInfo? p = new(root); p is not null; p = p.Parent)
        {
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Capture directory must not contain reparse points.");
            }
        }

        Directory.CreateDirectory(root);
        RecoveryPath = Path.Combine(root, "recovery-required.json");
        DirectoryPath = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        WriteNew("session.json", new
        {
            SourceSnapshot = typeof(Session).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SourceSnapshot")?.Value,
            Schema = 1,
            Tool = "AllyXLab",
            Version = "0.1.0",
            StartedUtc = DateTime.UtcNow,
            ExeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Environment.ProcessPath!))),
            Notice = "No machine/user name, serial number, raw PnP path or arbitrary keyboard input is intentionally collected. ASUS raw reports may contain device-specific payloads; review before sharing."
        });
    }
    internal void WriteNew(string file, object data)
    {
        using var output = new FileStream(Path.Combine(DirectoryPath, file), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(output, data, SessionLog.Json); output.Flush(true);
    }
    internal void Observation(string label, object data) => WriteNew($"{++_sequence:D3}-operator.json", new { Label = label, Utc = DateTime.UtcNow, Data = data });
    internal void ConfirmRecovery(string explanation)
    {
        Observation("operator-restoration-confirmation", explanation);
        if (File.Exists(RecoveryPath))
        {
            File.Delete(RecoveryPath);
        }
    }
    internal async Task<Result> RunAsync(Request request, Action<string> status)
    {
        if (Process is not null)
        {
            throw new InvalidOperationException("A step is already running.");
        }

        if (Limits.Mutates(request.Action) && File.Exists(RecoveryPath))
        {
            throw new InvalidOperationException("An earlier action needs restoration confirmation. Review its recovery file first.");
        }

        int sequence = ++_sequence;
        WriteNew($"{sequence:D3}-request.json", request);
        string nonce = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(Environment.ProcessId.ToString()); start.ArgumentList.Add(nonce);
        using var process = new Process { StartInfo = start };
        Process = process;
        bool checkpoint = false;
        Result? result = null;
        try
        {
            process.Start();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Nonce = nonce, Request = request }, Program.WireJson));
            await process.StandardInput.FlushAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                string? line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 32 * 1024 * 1024)
                {
                    throw new IOException("Worker report exceeds its bound.");
                }

                using var message = JsonDocument.Parse(line);
                string? kind = message.RootElement.GetProperty("Kind").GetString();
                JsonElement data = message.RootElement.GetProperty("Data");
                if (kind == "checkpoint")
                {
                    if (checkpoint)
                    {
                        throw new IOException("Duplicate hardware checkpoint refused.");
                    }

                    WriteNew($"{sequence:D3}-original.json", data);
                    using (var pending = new FileStream(RecoveryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        JsonSerializer.Serialize(pending, new { Session = DirectoryPath, Original = data.Clone() }, SessionLog.Json); pending.Flush(true);
                    }
                    checkpoint = true;
                    status("Original state saved. Performing the selected action; restoration follows.");
                    await process.StandardInput.WriteLineAsync("ACK"); await process.StandardInput.FlushAsync();
                }
                else if (kind == "progress")
                {
                    status(data.GetProperty("Message").GetString() + " • " + data.GetProperty("Seconds").GetInt32() + " seconds");
                }
                else if (kind == "result")
                {
                    result = data.Deserialize<Result>(SessionLog.Json);
                }
                else if (kind == "worker-error")
                {
                    throw new InvalidOperationException(data.GetString());
                }
            }
            await process.WaitForExitAsync(deadline.Token);
            string diagnostic = await stderr;
            if (diagnostic.Length > 0)
            {
                WriteNew($"{sequence:D3}-worker-stderr.json", diagnostic[..Math.Min(diagnostic.Length, 16384)]);
            }

            if (result is null)
            {
                throw new IOException("Worker exited without a complete result.");
            }

            WriteNew($"{sequence:D3}-result.json", result);
            Results.Add(result);
            if (checkpoint && result.Cleanup == "restored-readback-matched")
            {
                File.Delete(RecoveryPath);
            }

            return result;
        }
        catch (Exception e)
        {
            try { if (!process.HasExited) { process.Kill(true); } } catch (InvalidOperationException) { }
            WriteNew($"{sequence:D3}-incomplete.json", new { Error = e.Message, CheckpointSaved = checkpoint, Cleanup = checkpoint ? "UNKNOWN: inspect recovery-required.json and restore with OEM controls" : "No acknowledged hardware checkpoint" });
            throw;
        }
        finally { Process = null; }
    }
    internal void Cancel()
    {
        if (Process is { HasExited: false } process)
        {
            try { process.StandardInput.WriteLine("CANCEL"); process.StandardInput.Flush(); } catch (IOException) { }
        }
    }
    internal string Export(string destination)
    {
        if (Process is not null)
        {
            throw new InvalidOperationException("Finish or stop the current action first.");
        }

        if (File.Exists(destination))
        {
            throw new IOException("Choose a new ZIP filename; existing exports are never overwritten.");
        }

        var parent = new DirectoryInfo(Path.GetDirectoryName(destination)!);
        for (DirectoryInfo? p = parent; p is not null; p = p.Parent)
        {
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Export path contains a reparse point.");
            }
        }

        WriteNew($"{++_sequence:D3}-analysis.json", Analysis.Summarize(Results));
        string staging = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream output = new(staging, FileMode.CreateNew, FileAccess.Write))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    var manifest = new List<object>();
                    long total = 0;
                    string[] files = Directory.GetFiles(DirectoryPath).Order().ToArray();
                    if (files.Length > 512)
                    {
                        throw new IOException("Session has too many files to export.");
                    }

                    foreach (string file in files)
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Extension != ".json")
                        {
                            throw new IOException("Unexpected file or reparse point in capture session.");
                        }

                        if (info.Length > 32 * 1024 * 1024 || (total += info.Length) > 128 * 1024 * 1024)
                        {
                            throw new IOException("Capture exceeds export size bound.");
                        }

                        byte[] bytes = File.ReadAllBytes(file);
                        string name = Path.GetFileName(file);
                        using (var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open())
                        {
                            entry.Write(bytes);
                        }

                        manifest.Add(new { File = name, Bytes = bytes.Length, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) });
                    }
                    using var manifestStream = archive.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifestStream, manifest, SessionLog.Json);
                }
                output.Flush(true);
            }
            File.Move(staging, destination, false);
            return destination;
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }
}
