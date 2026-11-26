using System.Text.Json.Serialization;

namespace Nse.Model;

// The record kinds of the story ledger. Records are immutable: an edit is a retire plus a put.
// Knowledge, false belief, lie and the reader's ignorance are never stored. They are derived
// from these records at query time, so correcting one record corrects every answer built on it.

public enum EntityKind { Character, Place, Object, Group, Narrator }

/// <summary>Whether the author has settled a proposition, has not yet, or means to leave it open.</summary>
public enum Resolution { Decided, Open, Ambiguous }

public enum Stance { HoldsTrue, HoldsFalse, Undecided, Unaware }

public enum Strength { Certain, Believes, Suspects }

/// <summary>How a belief was acquired. The first five need a source that the holder was present at.</summary>
public enum Channel { Witnessed, TookPart, Told, Overheard, Read, Inferred, Assumed, Background, Explicit }

public enum Directness { Stated, Entailed, Inferred, Speculative }

public enum DisclosureMode { Stated, Implied, Focalized, AssertedByCharacter, Inferable, Posed }

public enum AssertionKind { Dialogue, Narration, Document, Thought }

public enum Sincerity { Sincere, Insincere, NotApplicable }

public enum ParticipantRole { Actor, Witness, Addressee }

public enum Approval { Provisional, Ratified, Rejected }

/// <summary>Whether a scene's place in story time was given outright or only follows from where it sits in the telling.</summary>
public enum TimeBasis { Asserted, FollowsTelling }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Entity), "entity")]
[JsonDerivedType(typeof(SceneRecord), "scene")]
[JsonDerivedType(typeof(SceneTime), "scene_time")]
[JsonDerivedType(typeof(Proposition), "proposition")]
[JsonDerivedType(typeof(FactChange), "fact_change")]
[JsonDerivedType(typeof(Event), "event")]
[JsonDerivedType(typeof(Assertion), "assertion")]
[JsonDerivedType(typeof(BeliefChange), "belief_change")]
[JsonDerivedType(typeof(Disclosure), "disclosure")]
[JsonDerivedType(typeof(Placement), "placement")]
[JsonDerivedType(typeof(Rule), "rule")]
[JsonDerivedType(typeof(Embargo), "embargo")]
[JsonDerivedType(typeof(Secret), "secret")]
[JsonDerivedType(typeof(SceneContract), "scene_contract")]
public abstract record LedgerRecord
{
    [JsonPropertyOrder(-10)]
    public required string Id { get; init; }

    /// <summary>
    /// Who made the record: "author", "import", or "agent", which may be followed by a colon and a model name.
    /// A record that comes in by a scene submission or a proposal without saying is given the name of whoever sent it.
    /// </summary>
    [JsonPropertyOrder(90)]
    public string Origin { get; init; } = "author";

    [JsonPropertyOrder(91)]
    public string? Note { get; init; }

    /// <summary>The name this kind has in ledger files: the "kind" each record is written with.</summary>
    [JsonIgnore]
    public string KindName => KindNames[GetType()];

    private static readonly Dictionary<Type, string> KindNames =
        typeof(LedgerRecord).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToDictionary(a => a.DerivedType, a => (string)a.TypeDiscriminator!);
}

public sealed record Entity : LedgerRecord
{
    public required EntityKind Type { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>A persona is how the story presents a person before saying who they are. Beliefs attach to the person.</summary>
    public string? PersonaOf { get; init; }
}

public sealed record SceneRecord : LedgerRecord
{
    public string? Title { get; init; }
    public required string Pov { get; init; }
    public IReadOnlyList<string> Cast { get; init; } = [];
    public string? Place { get; init; }
}

public sealed record SceneTime : LedgerRecord
{
    public required string Scene { get; init; }
    public required double StoryKey { get; init; }
    public string? Label { get; init; }
    public TimeBasis Basis { get; init; } = TimeBasis.Asserted;
}

/// <summary>A belief about a belief: "Daniel believes P", tracked as a proposition of its own.</summary>
public sealed record NestedBelief(string Holder, string Prop, Stance? Stance = null);

public sealed record Proposition : LedgerRecord
{
    public required string Text { get; init; }
    public IReadOnlyList<string> About { get; init; } = [];
    public Resolution Resolution { get; init; } = Resolution.Decided;
    public NestedBelief? Nested { get; init; }
}

/// <summary>A proposition becomes true or false in the story world. A null story key means "from the outset".</summary>
public sealed record FactChange : LedgerRecord
{
    public required string Prop { get; init; }
    public required bool Value { get; init; }
    public double? StoryKey { get; init; }
}

public sealed record Event : LedgerRecord
{
    public string? Scene { get; init; }
    public required double StoryKey { get; init; }
    public required string Summary { get; init; }
    public IReadOnlyDictionary<string, ParticipantRole> Participants { get; init; } =
        new Dictionary<string, ParticipantRole>();
    public string? Quote { get; init; }
    public string? QuoteScene { get; init; }
}

/// <summary>The text has someone claim a proposition. This says nothing about whether it is true.</summary>
public sealed record Assertion : LedgerRecord
{
    public required string Scene { get; init; }
    public required double StoryKey { get; init; }
    public required AssertionKind Type { get; init; }
    public required string Speaker { get; init; }
    public string? Focalizer { get; init; }
    public IReadOnlyList<string> Addressees { get; init; } = [];
    public IReadOnlyList<string> Overhearers { get; init; } = [];
    public string? ReadBy { get; init; }
    public required string Prop { get; init; }

    /// <summary>False when the speaker denies the proposition.</summary>
    public bool Polarity { get; init; } = true;
    public Sincerity? Sincerity { get; init; }
    public required string Quote { get; init; }
}

public sealed record BeliefChange : LedgerRecord
{
    public required string Holder { get; init; }
    public required string Prop { get; init; }
    public required Stance Stance { get; init; }
    public Strength? Strength { get; init; }
    public required double StoryKey { get; init; }
    public string? Scene { get; init; }
    public required Channel Channel { get; init; }

    /// <summary>The event or assertion the belief came from.</summary>
    public string? Via { get; init; }
    public Directness Directness { get; init; } = Directness.Stated;
    public string? Quote { get; init; }
    public string? QuoteScene { get; init; }
}

/// <summary>What the reader is given about a proposition in one scene. Ordered by the telling, not by story time.</summary>
public sealed record Disclosure : LedgerRecord
{
    public required string Scene { get; init; }
    public required string Prop { get; init; }
    public required DisclosureMode Mode { get; init; }
    public required Stance ReaderStance { get; init; }
    public Strength? Strength { get; init; }
    public string? Quote { get; init; }
}

/// <summary>Where an object is kept from a point in story time on. The first typed, single-valued fact.</summary>
public sealed record Placement : LedgerRecord
{
    public required string Object { get; init; }
    public required string Place { get; init; }
    public required double StoryKey { get; init; }
    public string? Scene { get; init; }
    public required string Cause { get; init; }
    public string? Quote { get; init; }
    public string? QuoteScene { get; init; }
}

/// <summary>
/// The author's standing word on the story. "never_resolve" and "never_establish" keep a question open for the
/// whole book and are checked. "guidance" is the author's understanding of a person or a stretch of the story,
/// not a constraint: it is shown, in plain prose, in the contexts of the scenes it bears on, and checked against
/// nothing. Any other type is a constraint in words, shown where it holds.
/// </summary>
public sealed record Rule : LedgerRecord
{
    public required string Type { get; init; }
    public string? Prop { get; init; }
    public required string Text { get; init; }

    /// <summary>Where the rule holds. Left out, everywhere. A never-settle rule is about the whole book and takes none.</summary>
    public RuleScope? Scope { get; init; }
}

/// <summary>
/// Where a rule holds. Every part that is given must hold for a scene; a part left out holds everywhere. The span
/// is in story time, because a person's stages are: a flashback told late belongs to the stage it is set in.
/// </summary>
public sealed record RuleScope
{
    /// <summary>The people it bears on. It holds in a scene where one of them is present, as themselves or as a persona.</summary>
    public IReadOnlyList<string> Holders { get; init; } = [];

    /// <summary>The scenes it holds in, and no others.</summary>
    public IReadOnlyList<string> Scenes { get; init; } = [];

    /// <summary>It holds in scenes set at or after this scene's place in story time.</summary>
    public string? FromScene { get; init; }

    /// <summary>It holds in scenes set before this scene's place in story time, and not in that scene itself.</summary>
    public string? UntilScene { get; init; }
}

/// <summary>The reader must not be given a proposition before a scene.</summary>
public sealed record Embargo : LedgerRecord
{
    public required string Prop { get; init; }
    public required string UntilScene { get; init; }
    public IReadOnlyList<string> Tripwires { get; init; } = [];
}

/// <summary>The author's declaration that only the named few ever learn these facts.</summary>
public sealed record Secret : LedgerRecord
{
    public required IReadOnlyList<string> Props { get; init; }
    public required IReadOnlyList<string> KnownOnlyTo { get; init; }
}

/// <summary>The kinds of change a scene can be meant to make, or relied on to find already made.</summary>
public enum IntentKind { Belief, Disclosure, Placement, Fact }

/// <summary>
/// One thing a scene is meant to do, or one thing it relies on being so on entry. Which fields matter depends
/// on the kind: a belief names a holder and a proposition; a disclosure a proposition; a placement an object;
/// a fact a proposition. A field that is left out matches anything.
/// </summary>
public sealed record Intent
{
    public required IntentKind Change { get; init; }
    public string? Holder { get; init; }
    public string? Prop { get; init; }

    /// <summary>For a belief, the holder's stance. For a disclosure, the reader's.</summary>
    public Stance? Stance { get; init; }
    public Strength? Strength { get; init; }
    public DisclosureMode? Mode { get; init; }
    public string? Object { get; init; }
    public string? Place { get; init; }
    public bool? Value { get; init; }
}

/// <summary>
/// What a scene is meant to do, agreed before it is drafted: the desired state. After drafting, what the
/// scene is recorded as doing is compared with it. An intended change that is missing and a change that was
/// not planned are both reported. A scene has at most one contract in force; an edit is a retire and a put.
/// </summary>
public sealed record SceneContract : LedgerRecord
{
    public required string Scene { get; init; }

    /// <summary>What happens, in the author's words. Not checked.</summary>
    public string? Outline { get; init; }

    /// <summary>The changes the scene is meant to make.</summary>
    public IReadOnlyList<Intent> Intends { get; init; } = [];

    /// <summary>What the scene relies on being so on entry.</summary>
    public IReadOnlyList<Intent> Relies { get; init; } = [];
}
