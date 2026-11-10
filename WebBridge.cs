using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Nowring;

/// <summary>
/// Мост для Discord в браузере. Сайт Discord не принимает локальные Rich Presence, поэтому программа
/// открывает локальный WebSocket (127.0.0.1:1337) в формате arRPC, а плагин Vencord «WebRichPresence (arRPC)»
/// в браузере подключается к нему и показывает активность.
/// </summary>
static class WebBridge
{
    const int Port = 1337;
    const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    static TcpListener? _listener;
    static readonly List<TcpClient> Clients = new();
    static readonly object W = new();
    static string? _last;

    public static void Start()
    {
        if (_listener != null) return;
        try
        {
            var l = new TcpListener(IPAddress.Loopback, Port);
            l.Start();
            _listener = l;
            _ = Task.Run(() => AcceptLoop(l));
            Log.Write("Веб-мост запущен: ws://127.0.0.1:" + Port);
        }
        catch (Exception ex)
        {
            _listener = null;
            Log.Write("Веб-мост не запущен (порт " + Port + " занят?): " + ex.Message);
        }
    }

    public static void Stop()
    {
        if (_listener == null) return;
        try { _listener.Stop(); } catch { }
        _listener = null;
        lock (W)
        {
            foreach (var c in Clients) { try { c.Close(); } catch { } }
            Clients.Clear();
            _last = null;
        }
    }

    static async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
            catch { break; }
            _ = Task.Run(() => Serve(c));
        }
    }

    static bool AllowedOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        var h = u.Host.ToLowerInvariant();
        return h == "discord.com" || h.EndsWith(".discord.com") || h == "discordapp.com" || h.EndsWith(".discordapp.com");
    }

    static async Task Serve(TcpClient c)
    {
        try
        {
            var s = c.GetStream();
            var buf = new byte[4096];
            var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n"))
            {
                int n = await s.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) return;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length > 16384) return;
            }
            var req = sb.ToString();
            var key = Regex.Match(req, @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var origin = Regex.Match(req, @"Origin:\s*(\S+)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (key.Length == 0) return;
            if (origin.Length > 0 && !AllowedOrigin(origin))
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"));
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));
            await s.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"));

            string? last;
            lock (W) { Clients.Add(c); last = _last; }
            if (last != null) Send(c, last);

            // входящие кадры нам не нужны: читаем, пока браузер не закроет соединение
            var rb = new byte[1024];
            while (await s.ReadAsync(rb, 0, rb.Length) > 0) { }
        }
        catch { }
        finally
        {
            lock (W) Clients.Remove(c);
            try { c.Close(); } catch { }
        }
    }

    static byte[] Frame(string text)
    {
        var p = Encoding.UTF8.GetBytes(text);
        var f = new List<byte> { 0x81 };
        if (p.Length < 126) f.Add((byte)p.Length);
        else if (p.Length <= ushort.MaxValue) { f.Add(126); f.Add((byte)(p.Length >> 8)); f.Add((byte)p.Length); }
        else { f.Add(127); for (int i = 7; i >= 0; i--) f.Add((byte)((long)p.Length >> (8 * i))); }
        f.AddRange(p);
        return f.ToArray();
    }

    static void Send(TcpClient c, string json)
    {
        try { lock (W) c.GetStream().Write(Frame(json)); }
        catch { }
    }

    static void Broadcast(string json)
    {
        TcpClient[] list;
        lock (W)
        {
            _last = json;
            list = Clients.ToArray();
        }
        foreach (var c in list) Send(c, json);
    }

    public static void Publish(string clientId, string name, object activity)
    {
        try
        {
            var node = JsonSerializer.SerializeToNode(activity)?.AsObject();
            if (node == null) return;
            node["application_id"] = clientId;
            node["name"] = name;
            node["instance"] = false;

            // формат arRPC: метки кнопок отдельно, ссылки в metadata
            if (node["buttons"] is JsonArray arr)
            {
                var labels = new JsonArray();
                var urls = new JsonArray();
                foreach (var b in arr)
                {
                    labels.Add(b!["label"]!.GetValue<string>());
                    urls.Add(b["url"]!.GetValue<string>());
                }
                node["buttons"] = labels;
                node["metadata"] = new JsonObject { ["button_urls"] = urls };
            }

            // браузеру нужны миллисекунды
            if (node["timestamps"] is JsonObject ts)
                foreach (var k in new[] { "start", "end" })
                    if (ts[k] is JsonNode v && v.GetValue<long>() < 100_000_000_000)
                        ts[k] = v.GetValue<long>() * 1000;

            var msg = new JsonObject { ["activity"] = node, ["pid"] = Environment.ProcessId, ["socketId"] = "nowring" };
            Broadcast(msg.ToJsonString());
        }
        catch (Exception ex)
        {
            Log.Write("Веб-мост: " + ex.Message);
        }
    }

    public static void Clear()
    {
        if (_listener == null) return;
        Broadcast(new JsonObject { ["activity"] = null, ["pid"] = Environment.ProcessId, ["socketId"] = "nowring" }.ToJsonString());
    }
}
