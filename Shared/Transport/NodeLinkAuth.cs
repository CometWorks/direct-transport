using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Shared.Transport;

internal static class NodeLinkAuth
{
    private static byte[][] m_accepted;

    public static bool Accepts(string token)
    {
        byte[] supplied = Hash(token ?? string.Empty);
        bool accepted = false;
        foreach (byte[] expected in Accepted)
            accepted |= FixedTimeEquals(supplied, expected);
        return accepted;
    }

    private static byte[][] Accepted => m_accepted ??= Load();

    private static byte[][] Load()
    {
        string current = Environment.GetEnvironmentVariable("SE_CLUSTER_JOIN_TOKEN_CURRENT")
            ?? Environment.GetEnvironmentVariable("SE_CLUSTER_JOIN_TOKEN");
        string previous = Environment.GetEnvironmentVariable("SE_CLUSTER_JOIN_TOKEN_PREVIOUS");
        var hashes = new List<byte[]>();
        if (!string.IsNullOrWhiteSpace(current))
            hashes.Add(Hash(current));
        if (!string.IsNullOrWhiteSpace(previous))
            hashes.Add(Hash(previous));
        return hashes.ToArray();
    }

    private static byte[] Hash(string value)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static bool FixedTimeEquals(byte[] left, byte[] right)
    {
        int difference = left.Length ^ right.Length;
        int count = Math.Min(left.Length, right.Length);
        for (int index = 0; index < count; index++)
            difference |= left[index] ^ right[index];
        return difference == 0;
    }
}
