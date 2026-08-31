using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Core.ImageIdentifiers;
using TaleWorlds.Localization;

namespace GameInterface.Services.MapEvents.PlayerPartyInteractions;

/// <summary>
/// Lets a stack of ordinary prisoners be offered in a player-to-player trade.
/// </summary>
/// <remarks>
/// <para>
/// Vanilla's <see cref="TransferPrisonerBarterable"/> takes a <c>Hero</c>, so it can only ever represent a
/// captured LORD. Every other prisoner a party holds - the looters, recruits and infantry that make up
/// essentially all of a real prison roster - had no barterable at all, so they never appeared on the barter
/// screen and could not be traded between players.
/// </para>
/// <para>
/// Only the offering half was missing. The receiving half already expected stacks of ordinary prisoners:
/// <c>PlayerPartyInteractionOutcomeHandler.AddPrisonerTransfers</c> resolves a plain
/// <see cref="CharacterObject"/> and clamps the requested count against
/// <c>sourceParty.PrisonRoster.GetElementNumber</c>, and <c>ApplyPrisoners</c> transfers that many. So this
/// completes a path that was already built rather than opening a new one.
/// </para>
/// <para>
/// Modelled on <see cref="PlayerPartyTroopBarterable"/>, which solves the same problem for party members.
/// The two differ only in which roster they read and in valuation: a prisoner is worth its ransom, not its
/// usefulness as a soldier.
/// </para>
/// </remarks>
internal class PlayerPartyPrisonerBarterable : Barterable
{
    private readonly Hero otherHero;
    private readonly PartyBase otherParty;
    private readonly TroopRosterElement prisonerRosterElement;

    public TroopRosterElement PrisonerRosterElement => prisonerRosterElement;

    public override int MaxAmount => prisonerRosterElement.Number;

    public override TextObject Name =>
        prisonerRosterElement.Character?.Name ?? new TextObject("{=MpPSKj5s}Troop");

    /// <remarks>
    /// Reuses the native prisoner id rather than inventing one: <c>BarterItemVisualBrushWidget</c> only
    /// enables its image widget for barterable type ids it recognises, so an unknown id renders the entry
    /// without a portrait.
    /// </remarks>
    public override string StringID => "transfer_prisoner_barterable";

    public PlayerPartyPrisonerBarterable(
        Hero ownerHero,
        Hero otherHero,
        PartyBase ownerParty,
        PartyBase otherParty,
        TroopRosterElement prisonerRosterElement) : base(ownerHero, ownerParty)
    {
        this.otherHero = otherHero;
        this.otherParty = otherParty;
        this.prisonerRosterElement = prisonerRosterElement;
    }

    /// <remarks>
    /// Deliberately empty. Every player-party barterable is inert: the trade is applied server-side from the
    /// agreed offer by <c>PlayerPartyInteractionOutcomeHandler</c>, so letting a barterable also apply itself
    /// on one client would transfer the same prisoners twice.
    /// </remarks>
    public override void Apply()
    {
    }

    public override int GetUnitValueForFaction(IFaction faction)
    {
        int value = GetRansomValue();

        if (faction == otherHero?.MapFaction || faction == otherParty?.MapFaction)
            return value;

        return -value;
    }

    private int GetRansomValue()
    {
        var character = prisonerRosterElement.Character;
        if (character == null) return 1;

        var ransomModel = Campaign.Current?.Models?.RansomValueCalculationModel;
        if (ransomModel == null) return Math.Max(1, character.Level);

        return Math.Max(1, ransomModel.PrisonerRansomValue(character));
    }

    public override ImageIdentifier GetVisualIdentifier()
    {
        if (prisonerRosterElement.Character == null) return null;

        return new CharacterImageIdentifier(CharacterCode.CreateFrom(prisonerRosterElement.Character));
    }
}
