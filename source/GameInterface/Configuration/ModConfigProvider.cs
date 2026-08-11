using ProtoBuf;

namespace GameInterface.Configuration;

public class ModConfigProvider
{
    /// <summary>What the session runs on until a config is loaded (server) or received (client).
    /// Built from an all-absent <see cref="ModOptionsData"/> so every option falls back to its
    /// documented default. It has to go through that constructor: the options struct declares no
    /// parameterless one, so a plain <c>new ModOptions()</c> is just <c>default</c> — the property
    /// initializers below never run and every option reads back false/0.</summary>
    public static ModOptions ModOptions = new(new ModOptionsData());

    public static void LoadModConfig(ModOptionsData modOptionsData)
    {
        ModOptions = new(modOptionsData);
    }
}

[ProtoContract(SkipConstructor = true)]
public readonly struct ModOptions
{
    [ProtoMember(1)]
    public readonly bool FastForwardEnabled { get; } = true;
    [ProtoMember(2)]
    public readonly bool AutoPauseEnabled { get; } = true;
    [ProtoMember(3)]
    public readonly bool ClientsCanUseCheats { get; } = false;
    [ProtoMember(4)]
    public readonly bool GoldFoodInfluenceChangeInSettlements { get; } = true;
    [ProtoMember(5)]
    public readonly GoldFoodChangeMode GoldFoodInfluenceChangeInBattles { get; } = GoldFoodChangeMode.OneDayMax;
    [ProtoMember(6)]
    public readonly bool GoldFoodInfluenceChangeForDisconnectedPlayers { get; } = false;
    [ProtoMember(7)]
    public readonly int PlayerBattleAiJoinWindowHours { get; } = 24;
    [ProtoMember(8)]
    public readonly bool SpeedLimitWhilePlayersInBattle { get; } = true;
    [ProtoMember(9)]
    public readonly int WandererLimit { get; } = 32;
    [ProtoMember(10)]
    public readonly bool WandererLimitScalesWithPlayers { get; } = false;
    [ProtoMember(11)]
    public readonly int PlayerKingdomClanTierRequired { get; } = 4;
    [ProtoMember(12)]
    public readonly bool SmithingStaminaRecoveryOutsideSettlements { get; } = true;
    [ProtoMember(13)]
    public readonly float SmithingStaminaRecoveryMultiplier { get; } = 0.1f;
    [ProtoMember(14)]
    public readonly float MaximumLootersMultiplier { get; } = 1f;
    [ProtoMember(15)]
    public readonly LordDefectionRetryMode LordDefectionRetries { get; } = LordDefectionRetryMode.Vanilla;
    // 16 and 17 belong to development's hero-execution options. This branch had claimed the same two
    // numbers for its siege options, so they are renumbered to 18-20: a ProtoMember number is the wire
    // identifier, and two fields sharing one silently corrupts config sync between server and clients.
    [ProtoMember(16)]
    public readonly bool EnableHeroExecutions { get; } = true;
    [ProtoMember(17)]
    public readonly bool EnablePlayerClanMemberExecutions { get; } = false;

    [ProtoMember(21)]
/// <summary>
    /// Whether a side that exceeds its share of the battle size has troops stood down to bring it back.
    /// </summary>
    /// <remarks>
    /// A considered choice rather than caution. Trimming the field is the only way
    /// to hold a side to its share once its men are already standing, but taking an agent off the field
    /// mid-battle has to satisfy every other system that believes in that agent - the movement poll, the damage
    /// router, the peers holding a puppet of it, the scoreboard, the campaign's casualty books. Getting any one
    /// of those wrong does not degrade the battle, it breaks it: two client freezes, troops that could not be
    /// killed because damage routed to an owner that no longer held them, and a second player unable to command
    /// his own army all came from this single feature.
    ///
    /// Re-enabled after every one of those was root-caused and fixed: the median-position read replaced with a
    /// field that cannot throw, the missing de-registration resolved by deferring to the existing rout path
    /// (which also restored the peer despawn that made troops stuck and unkillable), another player's troops
    /// excluded from withdrawal, and the scoreboard given an explicit decrement. The remaining risk is not
    /// zero, so the switch stays: turning it off costs only that a side may exceed its share after a large
    /// reinforcement, which is a working battle.
    /// </remarks>
    public readonly bool TrimFieldToBattleSize { get; } = true;
    [ProtoMember(18)]
    public readonly bool MilitiaJoinsSallyOut { get; } = true;
    [ProtoMember(19)]
    public readonly bool ResumeSiegeWhenEnemyRetreats { get; } = true;
    [ProtoMember(20)]
    public readonly bool GarrisonJoinsSiegeRelief { get; } = true;

    [ProtoMember(22)]
    /// <summary>
    /// Whether troops are stopped from breaking and fleeing when their morale gives out.
    /// </summary>
    /// <remarks>
    /// ON, because in a coop battle morale is measuring a fight that is not the one being fought. A side's
    /// morale is driven by casualties, by the men standing near you and by whether your formation still looks
    /// like a formation - and all three are distorted here: each client sees a different slice of the field,
    /// reinforcements arrive and are stood down again to hold the battle size, and agents are handed between
    /// owners on migration. Troops therefore break for reasons that have nothing to do with how the battle is
    /// going, which is exactly what was reported: men running away "for most of the time for no apparent
    /// reason".
    ///
    /// Turning it off restores vanilla routing, at the cost of that noise.
    /// </remarks>
    public readonly bool DisableBattleMorale { get; } = true;

    [ProtoMember(23)]
    /// <summary>
    /// Whether a kingdom led by a player is spared the AI's war and peace proposals.
    /// </summary>
    /// <remarks>
    /// ON, because the alternative is a ruler with no say over their own kingdom's wars. Single player never
    /// has this problem - as ruler you declare war and make peace yourself, and nothing goes around you. The
    /// co-op server runs every kingdom's AI and cannot tell which thrones have people on them, so it kept
    /// proposing wars and peaces for a player's kingdom and resolving them unasked.
    ///
    /// Only the AI's proposals are suppressed. The ruler's own diplomacy screen is untouched, and war can
    /// still arrive the ways it should: rebellion, crime, a call to war from an ally, hostility in the field.
    /// </remarks>
    public readonly bool PlayerLedKingdomsControlTheirOwnDiplomacy { get; } = true;

    /// <summary>
    /// Takes the configured value, or keeps the option's declared default when the config leaves it out.
    /// Spelled as a call rather than <c>??</c> so the constructor reads as one straight line per option:
    /// twenty null-coalesces in a row look like twenty branches, both to an analyzer and to anyone
    /// skimming for the single option they care about.
    /// </summary>
    private static T Or<T>(T? configured, T declaredDefault) where T : struct => configured ?? declaredDefault;

    public ModOptions(ModOptionsData modOptionsData)
    {
        FastForwardEnabled = Or(modOptionsData.FastForwardEnabled, FastForwardEnabled);
        AutoPauseEnabled = Or(modOptionsData.AutoPauseEnabled, AutoPauseEnabled);
        ClientsCanUseCheats = Or(modOptionsData.ClientsCanUseCheats, ClientsCanUseCheats);
        GoldFoodInfluenceChangeInSettlements = Or(modOptionsData.GoldFoodInfluenceChangeInSettlements, GoldFoodInfluenceChangeInSettlements);
        GoldFoodInfluenceChangeInBattles = Or(modOptionsData.GoldFoodInfluenceChangeInBattles, GoldFoodInfluenceChangeInBattles);
        GoldFoodInfluenceChangeForDisconnectedPlayers = Or(modOptionsData.GoldFoodInfluenceChangeForDisconnectedPlayers, GoldFoodInfluenceChangeForDisconnectedPlayers);
        PlayerBattleAiJoinWindowHours = Or(modOptionsData.PlayerBattleAiJoinWindowHours, PlayerBattleAiJoinWindowHours);
        SpeedLimitWhilePlayersInBattle = Or(modOptionsData.SpeedLimitWhilePlayersInBattle, SpeedLimitWhilePlayersInBattle);
        WandererLimit = Or(modOptionsData.WandererLimit, WandererLimit);
        WandererLimitScalesWithPlayers = Or(modOptionsData.WandererLimitScalesWithPlayers, WandererLimitScalesWithPlayers);
        PlayerKingdomClanTierRequired = Or(modOptionsData.PlayerKingdomClanTierRequired, PlayerKingdomClanTierRequired);
        SmithingStaminaRecoveryOutsideSettlements = Or(modOptionsData.SmithingStaminaRecoveryOutsideSettlements, SmithingStaminaRecoveryOutsideSettlements);
        SmithingStaminaRecoveryMultiplier = Or(modOptionsData.SmithingStaminaRecoveryMultiplier, SmithingStaminaRecoveryMultiplier);
        MaximumLootersMultiplier = Or(modOptionsData.MaximumLootersMultiplier, MaximumLootersMultiplier);
        LordDefectionRetries = Or(modOptionsData.LordDefectionRetries, LordDefectionRetries);
        EnableHeroExecutions = Or(modOptionsData.EnableHeroExecutions, EnableHeroExecutions);
        EnablePlayerClanMemberExecutions = Or(modOptionsData.EnablePlayerClanMemberExecutions, EnablePlayerClanMemberExecutions);
        MilitiaJoinsSallyOut = Or(modOptionsData.MilitiaJoinsSallyOut, MilitiaJoinsSallyOut);
        ResumeSiegeWhenEnemyRetreats = Or(modOptionsData.ResumeSiegeWhenEnemyRetreats, ResumeSiegeWhenEnemyRetreats);
        TrimFieldToBattleSize = Or(modOptionsData.TrimFieldToBattleSize, TrimFieldToBattleSize);
        GarrisonJoinsSiegeRelief = Or(modOptionsData.GarrisonJoinsSiegeRelief, GarrisonJoinsSiegeRelief);
        TrimFieldToBattleSize = Or(modOptionsData.TrimFieldToBattleSize, TrimFieldToBattleSize);
        DisableBattleMorale = Or(modOptionsData.DisableBattleMorale, DisableBattleMorale);
        PlayerLedKingdomsControlTheirOwnDiplomacy = Or(modOptionsData.PlayerLedKingdomsControlTheirOwnDiplomacy, PlayerLedKingdomsControlTheirOwnDiplomacy);
    }
}