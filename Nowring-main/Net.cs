using System;
using System.Collections.Generic;

namespace Nowring;

/// <summary>
/// Если сервис недоступен (заблокирован, нет сети), не долбим его на каждом треке:
/// помечаем на 10 минут и сразу переходим к следующему способу.
/// </summary>
static class Net
{
    static readonly Dictionary<string, DateTime> Down = new();

    public static bool IsUp(string host)
    {
        lock (Down) return !Down.TryGetValue(host, out var until) || until <= DateTime.UtcNow;
    }

    public static void Fail(string host, string reason)
    {
        lock (Down) Down[host] = DateTime.UtcNow.AddMinutes(10);
        Log.Write($"{host} недоступен 10 минут: {reason}");
    }

    public static void Fail(string host, Exception ex) => Fail(host, ex.GetType().Name + " " + ex.Message);
}
