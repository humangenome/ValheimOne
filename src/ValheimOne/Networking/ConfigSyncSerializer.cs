using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ValheimOne.Configuration;

namespace ValheimOne.Networking;

internal static class ConfigSyncSerializer
{
    public const int MaximumChunkTextBytes = 4000;

    public static string Serialize(FeatureRegistry registry)
    {
        var result = new StringBuilder();
        foreach (FeatureDefinition feature in registry.Features)
        {
            if (feature.Classification == FeatureClassification.ClientOnly ||
                feature.Classification == FeatureClassification.ServerOnly)
            {
                continue;
            }

            foreach (IConfigEntry entry in feature.Keys)
            {
                if (entry.Definition.IsSensitive)
                {
                    continue;
                }

                result.Append('[')
                    .Append(feature.Section)
                    .Append("] / ")
                    .Append(entry.Definition.Name)
                    .Append('=')
                    .Append(entry.GetSerializedValue())
                    .Append('\n');
            }
        }

        return result.ToString();
    }

    public static string ComputeHash(string serializedConfig)
    {
        byte[] input = Encoding.UTF8.GetBytes(serializedConfig);
        byte[] digest;
        using (SHA256 hash = SHA256.Create())
        {
            digest = hash.ComputeHash(input);
        }

        var result = new StringBuilder(digest.Length * 2);
        foreach (byte value in digest)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    public static IReadOnlyList<string> CreateChunks(string serializedConfig)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        int currentBytes = 0;

        using var reader = new StringReader(serializedConfig);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string terminatedLine = line + "\n";
            int lineBytes = Encoding.UTF8.GetByteCount(terminatedLine);
            if (lineBytes > MaximumChunkTextBytes)
            {
                throw new InvalidOperationException(
                    $"A serialized config line exceeds the {MaximumChunkTextBytes}-byte chunk limit.");
            }

            if (current.Length != 0 && currentBytes + lineBytes > MaximumChunkTextBytes)
            {
                chunks.Add(current.ToString());
                current.Clear();
                currentBytes = 0;
            }

            current.Append(terminatedLine);
            currentBytes += lineBytes;
        }

        if (current.Length != 0 || chunks.Count == 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }
}
