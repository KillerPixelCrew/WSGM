using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace WSGM.AllyXLab;

internal static class Program
{
    internal static readonly JsonSerializerOptions WireJson = new(SessionLog.Json) { WriteIndented = false };
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--worker")
        {
            RunWorker(args); return;
        }
        if (args.Length != 0)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        try { Application.Run(new MainForm()); }
        catch (Exception e) { MessageBox.Show(e.Message, "Ally X Lab could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private static void RunWorker(string[] args)
    {
        try
        {
            if (!Console.IsInputRedirected || !Console.IsOutputRedirected || !int.TryParse(args[1], out int parentId))
            {
                return;
            }

            using Process parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? initial = Console.ReadLine();
            if (initial is null || initial.Length > 8192)
            {
                return;
            }

            using var document = JsonDocument.Parse(initial);
            if (document.RootElement.GetProperty("Nonce").GetString() != args[2])
            {
                return;
            }

            Request request = document.RootElement.GetProperty("Request").Deserialize<Request>(SessionLog.Json)!;
            using var cancel = new CancellationTokenSource();
            using var ack = new AutoResetEvent(false);
            using var answers = new BlockingCollection<string>(1);
            _ = Task.Run(async () => { await parent.WaitForExitAsync(); cancel.Cancel(); });
            _ = Task.Run(() =>
            {
                string? line;
                while ((line = Console.ReadLine()) is not null)
                {
                    if (line == "ACK")
                    {
                        ack.Set();
                    }
                    else if (line.StartsWith("ANSWER:", StringComparison.Ordinal)) { answers.TryAdd(line[7..]); }
                    else if (line == "CANCEL")
                    {
                        cancel.Cancel();
                    }
                }
                cancel.Cancel();
            });
            int promptId = 0;
            Worker.Interaction = (data, token) =>
            {
                int id = ++promptId;
                Console.WriteLine(JsonSerializer.Serialize(new { Kind = "question", Data = new { Id = id, Prompt = data } }, WireJson));
                Console.Out.Flush();
                while (true)
                {
                    string answer = answers.Take(token);
                    string prefix = id + ":";
                    if (answer.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return answer[prefix.Length..];
                    }
                }
            };
            Worker.Progress = data => { Console.WriteLine(JsonSerializer.Serialize(new { Kind = "progress", Data = data }, WireJson)); Console.Out.Flush(); };
            Worker.Checkpoint = data =>
            {
                Console.WriteLine(JsonSerializer.Serialize(new { Kind = "checkpoint", Data = data }, WireJson));
                Console.Out.Flush();
                if (!ack.WaitOne(5000))
                {
                    throw new InvalidOperationException("Parent did not save the recovery checkpoint. No write is allowed.");
                }

                cancel.Token.ThrowIfCancellationRequested();
            };
            Result result = Worker.Run(request, cancel.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { Kind = "result", Data = result }, WireJson));
        }
        catch (Exception e)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Kind = "worker-error", Data = e.Message }, WireJson));
        }
    }
}
