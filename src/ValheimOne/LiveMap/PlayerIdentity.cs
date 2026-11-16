using System;
using System.Globalization;
using UnityEngine;

namespace ValheimOne.LiveMap;

// Two things /api/players needs that the game does not hand over directly: a key that tells the same player apart
// across visits, and the way the character faces.
internal static class PlayerIdentity
{
    private const int MaximumPlatformLength = 32;
    private const int MaximumUserIdLength = 64;

    // The player's account, written the way the game's own admin, ban and permitted lists write it: "Steam_<SteamID64>"
    // on a Steam server, and on a crossplay server the platform's own form ("Steam_...", "Xbox_...", ...), so one Steam
    // player gets the same key on both. It stays the same when the player leaves and joins again and when the server
    // restarts, unlike ZNetPeer.m_characterID.UserID (the `id` the map has always sent), which is the player's game
    // session and is new each time their game starts. Empty when the connection gives nothing usable.
    internal static string PlatformId(ISocket? socket)
    {
        if (socket == null)
        {
            return string.Empty;
        }

        string host;
        try
        {
            host = (socket.GetHostName() ?? string.Empty).Trim();
        }
        catch
        {
            // A connection that is closing can throw here; that player simply has no key in this snapshot.
            return string.Empty;
        }

        if (socket is ZSteamSocket)
        {
            // A Steam connection names the player by the bare SteamID64, as ZNet's own player list reads it.
            return ulong.TryParse(host, NumberStyles.None, CultureInfo.InvariantCulture, out ulong steamId) &&
                   steamId != 0UL &&
                   host.Length <= 20
                ? "Steam_" + steamId.ToString(CultureInfo.InvariantCulture)
                : IsPlatformForm(host) ? host : string.Empty;
        }

        // A crossplay (PlayFab) connection already gives "<Platform>_<id>".
        return IsPlatformForm(host) ? host : string.Empty;
    }

    // "<Platform>_<id>": a platform name of letters and digits that starts with a letter, and an id of letters, digits,
    // '.', '-' or '_' that is not "0". Anything else (an address, an empty id, odd characters) is not a key.
    internal static bool IsPlatformForm(string value)
    {
        int separator = value.IndexOf('_');
        if (separator < 1 || separator > MaximumPlatformLength || separator == value.Length - 1)
        {
            return false;
        }

        if (!IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (int index = 1; index < separator; index++)
        {
            if (!IsAsciiLetter(value[index]) && !IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        string userId = value.Substring(separator + 1);
        if (userId.Length > MaximumUserIdLength || userId == "0")
        {
            return false;
        }

        for (int index = 0; index < userId.Length; index++)
        {
            char character = userId[index];
            if (!IsAsciiLetter(character) && !IsAsciiDigit(character) &&
                character != '.' && character != '-' && character != '_')
            {
                return false;
            }
        }

        return true;
    }

    // Which way the character's body faces, in compass degrees (0 = +z, 90 = +x, the same scale as headingDeg), read
    // from the rotation the player's own game sends with its player object. The body turns with the player's view when
    // they stand still and with their movement when they move, so unlike headingDeg (worked out from two positions,
    // and kept while the player stands still) it follows a player who turns on the spot. False when the server has no
    // player object for them.
    internal static bool TryFacingDeg(ZDO? playerZdo, out float facingDeg)
    {
        facingDeg = 0f;
        if (playerZdo == null)
        {
            return false;
        }

        Vector3 forward = playerZdo.GetRotation() * Vector3.forward;
        return TryCompassDeg(forward.x, forward.z, out facingDeg);
    }

    // The compass direction of a ground vector; false for a vector with no ground length (straight up or down).
    internal static bool TryCompassDeg(float x, float z, out float degrees)
    {
        degrees = 0f;
        if (float.IsNaN(x) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(z) ||
            (x * x) + (z * z) < 1e-6f)
        {
            return false;
        }

        double compass = ((Math.Atan2(x, z) * 180.0 / Math.PI) + 360.0) % 360.0;
        // One decimal, as the API sends it; 359.96 would round to 360.0, which is north again.
        compass = Math.Round(compass, 1);
        degrees = compass >= 360.0 ? 0f : (float)compass;
        return true;
    }

    private static bool IsAsciiLetter(char value) => (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');

    private static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';
}
