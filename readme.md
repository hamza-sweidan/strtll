
**Vibe coded slop, try to avoid**
# Narrative State Engine

A local tool that keeps evidence-backed story state for long fiction, so that an AI agent drafting or revising a scene knows what each character knows, what the reader has been told, and what must not be given away yet.

One core library and one console program with two front ends: a command line, and an MCP server on standard input and output. No HTTP, no background process, no model calls.

## How a story is kept

```text
manuscript/*.md     the prose. A scene starts at a marker line: <!-- scene: some_id -->
ledger/*.json       append-only change sets: the story's state and its history
ledger/proposals/   changes an agent has asked for and the author has not decided on; not part of the story
ledger/.lock        held while a change set is appended; stays in place, ignored by git
work/tasks/         what a session was in the middle of, one file for each scene with work open on it; ignored by git
.nse/index.sqlite   derived from the manuscript and the change sets; delete it whenever you like
```

- **The ledger is the truth.** Each change-set file names the hash of the one before it, and its own id is a hash of what it says, so an edited or reordered file, or one missing from the middle, is refused on load.
- **Nothing derived is stored.** Knowledge, false belief, lie and what the reader does not yet know are computed from the records for whatever point is asked about. Correct one record and every answer built on it is corrected.
- **Two orders.** People, objects and facts are asked about in story time. The reader is asked about in the order the scenes are told, which is the order of the manuscript files.
- **Evidence is a quote.** A record that says something happens in a scene quotes the scene, and the quote has to be in its text word for word.
- **Agents propose, the author decides.** What an agent submits for a scene is applied as provisional. Anything else it wants changed is held as a proposal. Only the author ratifies or rejects.
- **Ties are reported, not guessed.** Records that share a story time and disagree come back as unresolved.
- **The index is a cache.** If it cannot be rebuilt or read, the answer is computed from the files and is the same.

## Build and test

Needs the .NET 10 SDK.

```powershell
dotnet test Nse.slnx
```

If the SDK was unpacked into the user profile instead of installed system-wide, use the wrapper, which finds it there and turns telemetry off:

```powershell
./eng/dotnet.ps1 test Nse.slnx
```

## Try it

`examples/harbour-letter` is a twelve-scene story with its ledger of sixteen change sets.

```powershell
./eng/dotnet.ps1 run --project src/Nse.Cli -- verify --dir examples/harbour-letter
```

```powershell
./eng/dotnet.ps1 run --project src/Nse.Cli -- state-at --scene sc_07 --holder ch_daniel --dir examples/harbour-letter
```

## Commands

| Command | Does |
|---|---|
| `nse init` | Creates `manuscript/` and `ledger/` for a new story |
| `nse put FILE` | Checks a change set against the ledger and the manuscript and appends it only if nothing is wrong. The author's own command: it can write anything, so it asks first and runs only at a terminal |
| `nse submit-scene FILE` | An agent's account of a scene it drafted or revised: the records the text supports, and the manifest id of the context it was written from. Checked like `put`; refused with the exact difference if the story's state has moved under the draft, and if it records an embargoed fact as said on the page without saying what the reader is given by it; applied as provisional; then compared with the scene's contract |
| `nse propose FILE` | Holds a change set for the author to decide on. Checked like `put`; nothing is applied. For whatever a scene submission may not carry: a rule, an embargo, a contract, a retcon, retiring a record the author ratified |
| `nse review [ID] [--brief] [--plain]` | What waits on the author: each held proposal with what it would do, why it is the author's to decide and whether it would still apply; and every provisional record, by the change set that brought it. `--plain` gives each record as a sentence in plain words |
| `nse ratify ID ...`, `nse reject ID ...` | The author's decision. An id is a record, a change set or a proposal. All of it is done or none of it. What it comes to is shown in full for confirmation first; runs only at a terminal |
| `nse sync` | Re-reads the manuscript, rebuilds the index, and reports evidence that is no longer in the text, scenes whose story time no longer fits the telling, anything now stated to the reader ahead of an embargo, and scenes that differ from their contract |
| `nse verify` | The same, after checking the ledger's chain |
| `nse state-at` | The state on entry to a scene or at the end: one person, one fact, the reader, an object, or all of it. A fact that is not so yet says when it becomes so. With `--holder` and `--prop` together it also shows the passage behind what that person has on that fact, the words quoted and the line they stand on, and when they have nothing, whether they were there when it was said and what they come by later. `--head N` answers as of an older ledger head |
| `nse scene-context` | The context for drafting or revising one scene: what must not be stated to the reader, with the hints the reader has had so far; what is so that nobody present has a record on, and what has become so since the teller was last on stage, whoever it is about; where each cast member believes the opposite, is shown not to know, is shut out by a secret, or was not there for what the teller last came by; beliefs on entry, and what each has only heard said; where things are; the reader; which prose to read. Silence is not listed. The rule for it is said once: a fact that is not under a person's name is one they have no record on, which is not the same as not knowing. All of it is the state on entry: a scene may give someone a fact they lacked, with or without a contract; anyone may reason their way to a view of their own, right or wrong, which the narration does not confirm; and a hint to the reader is not a statement. Each fact is worded once and named by id after that. Fails with `BUDGET_INSUFFICIENT` if what must be there does not fit `--budget`; a part that may be left out is left out whole, and named, and `--part TITLE` prints any one part by itself and in full. From an agent's shell, taking it opens a task for the scene. At a terminal, or with `--head` or `--no-task`, it is only a look; `--take` opens the task from a terminal too |
| `nse task-note FILE` | Adds to the task open on a scene what only whoever is working knows: what was decided, what is still open, what comes next, how far the draft has got |
| `nse brief` | The map a session starts from: scenes in telling order, any work in progress that an earlier session left, how much is recorded, what awaits review. `--task SCENE` shows that scene's open task in full |
| `nse ripple ID`, `nse pulse`, `nse weave` | The weave, the story as a signal graph: what a change to one record would disturb, with paths; the tension field scene by scene; the records whose change would reach furthest, or a Mermaid picture around one |
| `nse diagram VIEW [--holder ID]` | The story at a glance, as Markdown with Mermaid, generated and never edited: `knowledge` (one person, scene by scene), `secrets`, `objects` or `divergence` |
| `nse lint` | Suggestions for the author, never findings: reveals with no earlier hint, people who were there or were told and have no record, people who learn the same thing twice and statements that tell nobody anything new (how a fact that joins two details shows), embargoed facts said on the page with no record of what the reader was given, propositions that name nobody they are about |
| `nse what-if FILE` | What a change set would do, worked out in memory: whether it passes the checks, every answer that would change, scene by scene, and which drafting contexts would. Nothing is written. Exit 1 when it would be refused |
| `nse recap --holder ID (--at SCENE \| --end) [--truth]` | The story as one person knows it, in story-time order: what they took part in or saw, what was said to them or by them, what they came to believe. In their own terms; `--truth` adds the labels |
| `nse scan --scene ID [--file PATH]`, `nse scan --all` | Looks through a scene, a draft file, or every scene for the tripwires of the embargoes in force there. A hit is a passage to read, not a verdict. Exit 1 when anything matched |
| `nse audit --scene ID [--completeness]` | The brief for an auditor with a fresh context: where the scene's text is, what it must not give away, the scan's hits, and the form of the report. `--completeness` asks instead what the scene does of weight that no record says |
| `nse protocol` | Prints how an agent is told to work with the engine: the text the MCP server gives its client |
| `nse state-at --prop ID --history` | Every change to one fact: when the fact itself holds, who learned it when, how and from whom, and when the reader was given it |
| `nse state-at --object ID --history` | Every move of one object in story order: where it was kept from when, and the event and people that moved it |
| `nse mcp [--with-ratify]` | Serves `story_brief`, `scene_context`, `state_at`, `review_queue`, `recap`, `what_if`, `lint`, `ripple`, `pulse`, `scan_draft`, `audit_brief`, `submit_scene`, `propose_changes` and `task_note` over MCP, and with `--with-ratify` also `ratify`. The tools print what the commands print |
| `nse setup-claude` | Writes what Claude Code needs into the story folder: the MCP server, the hooks, the permission rules, the `scene-work` skill and `CLAUDE.md`. Files already there are kept unless `--force` is given. With `--with-auditor` also the `scene-auditor` agent: an audit is another model session, so nothing starts one unless the author asks |
| `nse hook EVENT` | What Claude Code's hooks run: `pre-edit`, `post-edit`, `stop`, `session-start` or `pre-compact`. Reads the event's JSON on standard input and answers in the form Claude Code reads. Never exits with 2 |
| `nse dump` | Every point-in-time answer as one deterministic JSON document |
| `nse rebuild` | Throws the index away and builds it again. Also how a ledger that is shorter than this folder has seen it is accepted as it stands; that is asked about first and done only at a terminal |

## Using it from Claude Code

Publish the engine, then set the story folder up from it:

```powershell
./eng/dotnet.ps1 publish src/Nse.Cli -c Release -o C:/path/to/nse
```

```powershell
dotnet C:/path/to/nse/nse.dll setup-claude --dir C:/path/to/story
```

It writes the MCP server entry, the hooks, the permission rules, a `scene-work` skill and `CLAUDE.md`, with this machine's paths to the engine. An audit of a scene by a fresh session is opt-in: `--with-auditor` writes the `scene-auditor` agent, and it is used when the author asks for an audit. Open Claude Code in the story folder and say yes when it asks whether to use the `nse` server. Run it again after moving the engine, and on each machine.

`--with-ratify`, which the written `.mcp.json` passes, adds the tool that carries out your decisions. Claude Code asks you before every call of it. Leave the flag out for any other client unless you know it does the same; without the flag an agent can still show you the queue, and you decide with `nse ratify` in your own terminal.

The permission rules allow every tool but `ratify`, which always asks. `scene_context` is not marked read-only: taken to work from, it writes the scene's task under `work/tasks/`. It changes nothing of the story.

## Limits

- Queries load the records that count as of a ledger head and compute in memory. Measured on the example laid end to end twenty times, 240 scenes and 7,760 records, in a debug build: an answer about one scene (its context, an audit brief, a scan) comes in under a second, most of it spent reading the ledger and the manuscript; a scan of every scene takes a second or two; the state dump takes five or six seconds and is 54 MB, because it says everything about everyone at every point; a what-if takes ten to twenty seconds, because it works out that dump and every scene's context twice over. Each costs in line with the size of what it answers, and for the last two that size grows with the square of the story. Nothing longer has been measured.
- A context grows with what the people in the scene hold and with what the reader is still to be told, not with people times facts. At that length the context of the last scene is about 108,000 characters, and it fits the default budget with four parts left out by name, chief among them the reader's state and the facts that are about nobody in the scene. The context of an early scene does not fit. Everything the reader is first told in the 230 scenes after it is on its list of what must not be revealed, which is in the part that is never left out and needs about 20,000 tokens by itself, so `scene-context` fails with `BUDGET_INSUFFICIENT` until the budget is raised.
- A record's place in story time is always given outright. Inheriting it from the scene is not implemented.
- "By the end of a scene" means up to the next scene in story time, and at most five units on.
- A context includes all of the point-of-view character's beliefs. Cutting them to what touches the scene is not done.
- There is no revision history of the manuscript, only the current hash. Re-anchoring quotes that moved is not implemented.
- A quote may occur more than once in its scene; which occurrence is meant is not recorded.
- A quote must spell an accented letter the way the text does: the engine runs without the Unicode tables that would fold the two spellings.
- A scene's title is not scanned for tripwires; only its paragraphs are.
- The weave's strand weights are fixed in code and are one set of judgements; nothing tunes them.
- The rules that keep an agent's file tools out of `ledger/` are written by `nse setup-claude`, and the pre-edit hook refuses such edits too. A shell command can still write there; the engine notices an edited or missing change set, and does not prevent one.
- Tested on Windows only. The file locking has not been exercised on Linux or macOS.
