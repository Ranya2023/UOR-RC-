using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Remco;

/// <summary>
/// Receives the phone's screen when it comes from an iPhone. Apple makes screen
/// sharing run in a separate little program (a "broadcast extension") that cannot
/// use the app's own connection, so it connects here instead — one JSON line per
/// picture, exactly like the Android frames. A token from the phone is required,
/// so nothing else on the Wi-Fi can push pictures onto the projector.
/// </summary>
internal sealed class PhoneScreenServer : IDisposable
{
    public const int Port = 47803;
    private TcpListener? _listener;
    private volatile bool _running;
    private string _token = "";

    /// <summary>(base64 jpeg, width, height)</summary>
    public event Action<string, int, int>? Frame;
    public event Action<bool>? Streaming;
    public event Action<string>? Log;

    public void SetToken(string token) { _token = token; Start(); }

    public void Start()
    {
        if (_running || _token.Length == 0) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start(4);
            _running = true;
            _ = Task.Run(Accept);
        }
        catch (Exception ex) { Log?.Invoke("Phone screen port busy: " + ex.Message); }
    }

    private async Task Accept()
    {
        while (_running)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(); }
            catch { if (!_running) break; continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            bool started = false;
            try
            {
                client.ReceiveTimeout = 15000;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                string? hello = reader.ReadLine();
                if (hello == null) return;
                using (var doc = JsonDocument.Parse(hello))
                {
                    var m = doc.RootElement;
                    string token = m.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                    if (_token.Length == 0 || token != _token) { Log?.Invoke("A phone screen connection was refused (wrong token)."); return; }
                }
                started = true;
                Streaming?.Invoke(true);
                Log?.Invoke("📱 The phone's screen is now showing.");

                string? line;
                while (_running && (line = reader.ReadLine()) != null)
                {
                    if (line.Length < 8) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var m = doc.RootElement;
                        if (!m.TryGetProperty("img", out var img)) continue;
                        Frame?.Invoke(img.GetString() ?? "",
                            m.TryGetProperty("w", out var w) ? w.GetInt32() : 0,
                            m.TryGetProperty("h", out var h) ? h.GetInt32() : 0);
                        writer.WriteLine("{\"t\":\"ack\"}");     // one picture at a time
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                if (started) { Streaming?.Invoke(false); Log?.Invoke("📱 The phone's screen stopped."); }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }
}
