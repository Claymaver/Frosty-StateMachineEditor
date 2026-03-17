# Creating SeqFLOWs

SeqFLOW controllers define attack chains, ability sequences, and other state flows for characters. You can create new ones for custom characters or add new abilities to existing ones.

## How To Create

1. In the tree explorer, **right-click** a character group (or its "SeqFlows" node)
2. Select **Create SeqFLOW...**
3. **Pick a template** — Choose an existing SeqFLOW to use as the base. The template provides the conduit track structure. You can pick any character's SeqFLOW as a template.
4. **Name your controller**:
   - **Prefix**: `A` (first-person) or `3P` (third-person)
   - **Character**: The character name (e.g., `Mace`, `Ahsoka`)
   - **Action Path**: What this chain does (e.g., `Attack`, `Frenzy`, `Dash`)
   - The preview shows the full name: `A.Mace.Attack.SeqFLOW`
5. Click **Create**

## What Happens Behind the Scenes

When you create a new SeqFLOW:

1. The template controller is **deep-copied** via FrostyClipboard (new GUIDs are generated)
2. All animation nodes are **stripped** — only the conduit track is kept
3. The conduit track's transitions are **cleared** (you'll wire up new nodes)
4. The controller and all its objects are **registered in AllControllerDatas** with correct AssetIndex values
5. A new character group is created in the tree if one doesn't exist for that character
6. The editor switches to Graph View showing your new (empty) controller

## After Creation

Your new SeqFLOW starts with just a conduit track node. To build an attack chain:

1. Use **Add Node** in the toolbar to add nodes from other characters (see [[Adding and Removing Nodes]])
2. Wire them together by dragging from output ports to input ports
3. Use **Commit Chain** to finalize the wiring

## Naming Convention

Controllers are linked to characters purely by their name. The game looks for controllers matching the pattern:

```
{Prefix}.{CharacterName}.{Action}.SeqFLOW
```

There's no separate "assignment" step — if the name matches, the game uses it.

## Tips

- **Start with a similar character's template** — If making an Attack chain for a new lightsaber hero, use Luke's or Anakin's Attack SeqFLOW as the template
- **The conduit track is essential** — It's the root node that connects to all other nodes in the chain. Every SeqFLOW needs one.
- **You can have multiple SeqFLOWs per character** — e.g., Attack, Frenzy, and Dash can all be separate controllers
