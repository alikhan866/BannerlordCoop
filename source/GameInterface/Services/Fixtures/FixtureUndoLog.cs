using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.Fixtures;

/// <summary>
/// The bookkeeping half of C20's fixture: what was changed, and how to put each thing back.
/// </summary>
/// <remarks>
/// Separated from the commands so it can be tested without a campaign. The rules below are the part most likely
/// to be subtly wrong, and every one of them fails silently when it is - a fixture that reports a clean restore
/// while leaving the world altered poisons every run after it, and nothing downstream would reveal the cause.
/// </remarks>
public sealed class FixtureUndoLog
{
    private readonly List<UndoEntry> entries = new List<UndoEntry>();

    public int Count => entries.Count;

    public IReadOnlyList<UndoEntry> Entries => entries;

    /// <summary>
    /// Records how to undo a change, keeping only the FIRST original seen for a given key.
    /// </summary>
    /// <remarks>
    /// A scenario that sets a hero's gold twice must restore to the value from before the fixture, not to the
    /// intermediate one it happened to pass through. Later records for the same key are therefore dropped
    /// rather than replacing or stacking on the earlier one.
    /// </remarks>
    public void Record(string key, string description, bool exact, Action undo)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("An undo entry needs a key.", nameof(key));
        if (undo == null) throw new ArgumentNullException(nameof(undo));
        if (entries.Any(entry => entry.Key == key)) return;

        entries.Add(new UndoEntry(key, description, exact, undo));
    }

    /// <summary>
    /// Undoes every recorded change, most recent first.
    /// </summary>
    /// <remarks>
    /// Reverse order because changes interact: a hero taken prisoner after their settlement changed hands has
    /// to be released before the settlement goes back. Reverse is the only ordering that is right in general
    /// rather than by luck.
    ///
    /// One failing undo does NOT abandon the others. A half-restored world is worse than a mostly-restored one,
    /// and the caller needs to know exactly which parts did not come back rather than losing the rest to the
    /// first exception.
    /// </remarks>
    public FixtureRestoreOutcome Restore()
    {
        var attempted = entries.Count;
        var restored = 0;
        var approximate = new List<string>();
        var failures = new List<string>();

        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            try
            {
                entry.Undo();
                restored++;
                if (!entry.Exact) approximate.Add(entry.Key);
            }
            catch (Exception exception)
            {
                failures.Add($"{entry.Key}: {exception.Message}");
            }
        }

        entries.Clear();
        return new FixtureRestoreOutcome(attempted, restored, approximate, failures);
    }

    public sealed class UndoEntry
    {
        public UndoEntry(string key, string description, bool exact, Action undo)
        {
            Key = key;
            Description = description;
            Exact = exact;
            Undo = undo;
        }

        public string Key { get; }

        public string Description { get; }

        /// <summary>False when the undo cannot perfectly reverse the change, only approximate it.</summary>
        public bool Exact { get; }

        public Action Undo { get; }
    }
}

/// <summary>What a restore actually managed, as opposed to what it attempted.</summary>
public sealed class FixtureRestoreOutcome
{
    public FixtureRestoreOutcome(int attempted, int restored, IReadOnlyList<string> approximate, IReadOnlyList<string> failures)
    {
        Attempted = attempted;
        Restored = restored;
        Approximate = approximate;
        Failures = failures;
    }

    public int Attempted { get; }

    public int Restored { get; }

    /// <summary>Keys that were undone, but only approximately.</summary>
    public IReadOnlyList<string> Approximate { get; }

    public IReadOnlyList<string> Failures { get; }

    /// <summary>
    /// True only when everything came back exactly. An approximate undo is NOT exact, deliberately: a caller
    /// that treats "restored" as "the world is as it was" would carry the difference into every later run.
    /// </summary>
    public bool Exact => Failures.Count == 0 && Approximate.Count == 0 && Restored == Attempted;
}
