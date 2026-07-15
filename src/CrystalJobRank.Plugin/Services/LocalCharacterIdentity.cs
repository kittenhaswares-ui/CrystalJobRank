using CrystalJobRank.Core;
using Dalamud.Plugin.Services;

namespace CrystalJobRank.Plugin.Services;

internal readonly record struct LocalCharacterIdentity(
    string CharacterName,
    uint WorldId,
    string WorldName)
{
    public string DisplayLabel => $"{CharacterName} · {WorldName}";
}

internal static class LocalCharacterIdentityReader
{
    public static bool TryRead(
        IClientState clientState,
        IPlayerState playerState,
        out LocalCharacterIdentity identity,
        out string unavailableReason)
    {
        identity = default;
        if (!clientState.IsLoggedIn || !playerState.IsLoaded)
        {
            unavailableReason = "Log into a character before creating or using a leaderboard identity.";
            return false;
        }

        var homeWorld = playerState.HomeWorld;
        if (!homeWorld.IsValid || homeWorld.RowId is 0 or > ushort.MaxValue)
        {
            unavailableReason = "Dalamud has not loaded this character's Home World yet.";
            return false;
        }

        try
        {
            var world = homeWorld.ValueNullable;
            if (!world.HasValue)
            {
                unavailableReason = "Dalamud could not resolve this character's Home World. Try again after login finishes.";
                return false;
            }

            var registration = Validation.NormalizeRegistration(
                playerState.CharacterName,
                homeWorld.RowId,
                world.Value.Name.ToString());
            identity = new LocalCharacterIdentity(
                registration.CharacterName,
                registration.WorldId,
                registration.WorldName);
            unavailableReason = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            unavailableReason = "Dalamud returned an incomplete character name or Home World. Try again after login finishes.";
            return false;
        }
        catch (InvalidOperationException)
        {
            unavailableReason = "Dalamud could not resolve this character's Home World. Try again after login finishes.";
            return false;
        }
    }
}
