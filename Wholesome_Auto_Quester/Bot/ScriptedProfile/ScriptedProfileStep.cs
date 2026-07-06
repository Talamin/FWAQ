using robotManager.Helpful;
using System.Collections.Generic;

namespace Wholesome_Auto_Quester.Bot.ScriptedProfile
{
    /// <summary>
    /// One ordered step of a hand-authored "profile" for a scripted zone the DB-driven quester can't derive on its own
    /// (currently the Death Knight start at Ebon Hold). Every field is verified against the world DB before it ships -
    /// the whole point of the profile is that it encodes the exact recipe (quest id, giver/turn-in npc, coords), so no
    /// value here is guessed. Actions grow as we add quest mechanics; the first slice only needs "pickup" / "turnin".
    /// </summary>
    public class ScriptedProfileStep
    {
        public string Action { get; set; }    // pickup | turnin | get-item-from-go | use-item | runeforge | interact-go | turnin-go | eye-of-acherus | ride-transporter | take-taxi | patrol | steal-horse | duel | into-realm | raise-ghouls | cannon | set-ground-mount | train | persuade | special-surprise | ambush | free-and-kill | portal | frost-wyrm | escort-battle | death-gate | acherus-battle | faction-finale | todo
        public int QuestId { get; set; }
        public string QuestName { get; set; }  // exact in-game LogTitle, for gossip-button matching
        public int Npc { get; set; }           // giver (pickup) / turn-in (turnin) creature entry
        public int GoEntry { get; set; }       // "get-item-from-go": the gameobject entry that yields ItemId
        public int ItemId { get; set; }        // item to obtain ("get-item-from-go") / use ("use-item")
        public string ItemName { get; set; }   // exact in-game name of ItemId - protected from sell/delete while active
        public int ResultItemId { get; set; }  // item whose presence marks this step done (e.g. use 38607 -> get 38631)
        public string ResultItemName { get; set; } // exact in-game name of ResultItemId - also sell/delete-protected
        public List<string> ProtectItems { get; set; } // extra quest-item names to sell/delete-protect (e.g. items that DROP during the quest, like "Scarlet Courier's Belongings"/"Message" - not just the ones referenced by ItemId/ResultItemId). ALWAYS list every quest item a step touches here so the inventory manager never trashes them.
        public int WaitMs { get; set; }        // post-action wait in ms (cast / spawn / server delay)
        public int Map { get; set; }           // WoW map id (Ebon Hold = 609)
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float TargetZ { get; set; }          // "ride-transporter"/"take-taxi": Z of the DESTINATION level; done when we're near it
        public List<float[]> Path { get; set; }     // "ride-transporter": waypoints [x,y,z] / "patrol": hotspots to roam
        public int GossipOption { get; set; }        // "take-taxi": gossip option index to click on the taxi NPC (e.g. the flight)
        public List<int> TargetEntries { get; set; } // "patrol": creature entries to attack for the objective(s)
        public List<int> GoEntries { get; set; }     // "patrol": gameobject entries to loot for the objective(s)
        public List<int> QuestIds { get; set; }      // "patrol": quests this step completes together (done when ALL are complete); defaults to [QuestId]
        public bool NoCombat { get; set; }           // if true, the combat states stand down while this step is current (e.g. a vehicle ride we must not interrupt)
        public bool DisablePlugins { get; set; }      // if true, ALL WRobot plugins are unloaded while this step is current (e.g. so AutoEquip can't swap a quest weapon back); restored when a normal step returns
        public int TrainId { get; set; }              // "train": a unique id per hard-coded training POINT (the DK trains several times as it levels), so each is tracked independently instead of a single one-shot flag
        public string Comment { get; set; }

        public Vector3 GetPosition => new Vector3(X, Y, Z);

        /// <summary>The <see cref="Path"/> as Vector3s (empty if none set).</summary>
        public List<Vector3> GetPath() =>
            Path == null ? new List<Vector3>() : Path.ConvertAll(p => new Vector3(p[0], p[1], p[2]));
    }
}
