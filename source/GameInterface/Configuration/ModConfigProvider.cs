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
    public readonly float LooterPartySizeMultiplier { get; } = 1f;
    /// <summary>
    /// How long a lord remembers being asked to defect.
    /// </summary>
    /// <remarks>
    /// AlwaysRetry rather than Vanilla, because vanilla's rule is a poor fit for co-op and reads as a bug.
    /// The blocker is <c>conversation_lord_from_ruling_clan_on_condition</c>, whose predicate is
    /// <c>Any(a =&gt; a.PersuadedHero == OneToOneConversationHero)</c> - it checks neither AGE nor SUCCESS. So a
    /// lord you already persuaded, whose barter then failed for an unrelated reason, answers "You have tried
    /// to persuade me before" and stays shut for an in-game YEAR, which is the only thing that prunes the
    /// record.
    ///
    /// In singleplayer that is survivable: one player, one conversation, and a barter that does not fail
    /// underneath you. In co-op the barter can be refused by the SERVER - a stale conversation context, a
    /// price check - and each refusal still leaves the attempt recorded, so lords burn out of reach through
    /// no decision of the player's.
    ///
    /// AlwaysRetry clears that lord's records at the start of each conversation. It does not make persuasion
    /// easier: a fresh attempt still rolls, and can still fail. It only makes him askable again.
    ///
    /// The shipped mod-config.default.json carries the same value, and
    /// <c>ModConfigTests.ShippedTemplate_ModOptions_AllBind_AndAreTheDefaults</c> holds the two together.
    /// </remarks>
    [ProtoMember(16)]
    public readonly LordDefectionRetryMode LordDefectionRetries { get; } = LordDefectionRetryMode.AlwaysRetry;
    [ProtoMember(17)]
    public readonly bool EnableHeroExecutions { get; } = true;
    [ProtoMember(18)]
    public readonly bool EnablePlayerClanMemberExecutions { get; } = false;
    [ProtoMember(19)]
    public readonly bool ShowPlayerNameplates { get; } = true;
    [ProtoMember(20)]
    public readonly bool PlayerWoundedBattleEntry { get; } = true;

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
    [ProtoMember(21)]
    public readonly bool TrimFieldToBattleSize { get; } = true;
    [ProtoMember(22)]
    public readonly bool MilitiaJoinsSallyOut { get; } = true;
    [ProtoMember(23)]
    public readonly bool ResumeSiegeWhenEnemyRetreats { get; } = true;
    [ProtoMember(24)]
    public readonly bool GarrisonJoinsSiegeRelief { get; } = true;

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
    [ProtoMember(25)]
    public readonly bool DisableBattleMorale { get; } = true;

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
    [ProtoMember(26)]
    public readonly bool PlayerLedKingdomsControlTheirOwnDiplomacy { get; } = true;

    public ModOptions(ModOptionsData modOptionsData)
    {
        FastForwardEnabled = modOptionsData.FastForwardEnabled ?? FastForwardEnabled;
        AutoPauseEnabled = modOptionsData.AutoPauseEnabled ?? AutoPauseEnabled;
        ClientsCanUseCheats = modOptionsData.ClientsCanUseCheats ?? ClientsCanUseCheats;
        GoldFoodInfluenceChangeInSettlements = modOptionsData.GoldFoodInfluenceChangeInSettlements ?? GoldFoodInfluenceChangeInSettlements;
        GoldFoodInfluenceChangeInBattles = modOptionsData.GoldFoodInfluenceChangeInBattles ?? GoldFoodInfluenceChangeInBattles;
        GoldFoodInfluenceChangeForDisconnectedPlayers = modOptionsData.GoldFoodInfluenceChangeForDisconnectedPlayers ?? GoldFoodInfluenceChangeForDisconnectedPlayers;
        PlayerBattleAiJoinWindowHours = modOptionsData.PlayerBattleAiJoinWindowHours ?? PlayerBattleAiJoinWindowHours;
        SpeedLimitWhilePlayersInBattle = modOptionsData.SpeedLimitWhilePlayersInBattle ?? SpeedLimitWhilePlayersInBattle;
        WandererLimit = modOptionsData.WandererLimit ?? WandererLimit;
        WandererLimitScalesWithPlayers = modOptionsData.WandererLimitScalesWithPlayers ?? WandererLimitScalesWithPlayers;
        PlayerKingdomClanTierRequired = modOptionsData.PlayerKingdomClanTierRequired ?? PlayerKingdomClanTierRequired;
        SmithingStaminaRecoveryOutsideSettlements = modOptionsData.SmithingStaminaRecoveryOutsideSettlements ?? SmithingStaminaRecoveryOutsideSettlements;
        SmithingStaminaRecoveryMultiplier = modOptionsData.SmithingStaminaRecoveryMultiplier ?? SmithingStaminaRecoveryMultiplier;
        MaximumLootersMultiplier = modOptionsData.MaximumLootersMultiplier ?? MaximumLootersMultiplier;
        LooterPartySizeMultiplier = modOptionsData.LooterPartySizeMultiplier ?? LooterPartySizeMultiplier;
        LordDefectionRetries = modOptionsData.LordDefectionRetries ?? LordDefectionRetries;
        EnableHeroExecutions = modOptionsData.EnableHeroExecutions ?? EnableHeroExecutions;
        EnablePlayerClanMemberExecutions = modOptionsData.EnablePlayerClanMemberExecutions ?? EnablePlayerClanMemberExecutions;
        ShowPlayerNameplates = modOptionsData.ShowPlayerNameplates ?? ShowPlayerNameplates;
        TrimFieldToBattleSize = modOptionsData.TrimFieldToBattleSize ?? TrimFieldToBattleSize;
        MilitiaJoinsSallyOut = modOptionsData.MilitiaJoinsSallyOut ?? MilitiaJoinsSallyOut;
        ResumeSiegeWhenEnemyRetreats = modOptionsData.ResumeSiegeWhenEnemyRetreats ?? ResumeSiegeWhenEnemyRetreats;
        GarrisonJoinsSiegeRelief = modOptionsData.GarrisonJoinsSiegeRelief ?? GarrisonJoinsSiegeRelief;
        DisableBattleMorale = modOptionsData.DisableBattleMorale ?? DisableBattleMorale;
        PlayerLedKingdomsControlTheirOwnDiplomacy = modOptionsData.PlayerLedKingdomsControlTheirOwnDiplomacy ?? PlayerLedKingdomsControlTheirOwnDiplomacy;
        PlayerWoundedBattleEntry = modOptionsData.PlayerWoundedBattleEntry ?? PlayerWoundedBattleEntry;
    }
}
