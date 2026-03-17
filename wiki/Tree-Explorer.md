# Tree Explorer

The left panel shows all characters and their state machine controllers, parsed from the EBX asset.

## Character Groups

Every controller in the state machine follows the SWBF2 naming convention:

```
{Prefix}.{Character}.{Action}.{Type}
```

For example: `A.Anakin.Attack.SeqFLOW`

- **Prefix**: `A` (first-person/all) or `3P` (third-person)
- **Character**: The hero or unit name
- **Action**: What the controller handles (Attack, Frenzy, Dash, etc.)
- **Type**: Controller type (SeqFLOW, SEQ, etc.)

The tree groups controllers by character, so all of Anakin's controllers appear under one "Anakin" node.

## Search

The search box at the top filters the tree in real-time. It matches against character names and controller names. Clear the search to show all characters again.

## Sorting

The sort dropdown offers different ordering options:

- **Category then Name** (default) — Groups nodes by their category (Lightsaber, Force, Movement, etc.) then alphabetically
- **Name** — Pure alphabetical
- **Sequence** — Orders by the chain sequence (useful for understanding attack flows)

## Right-Click Menu

Right-clicking a **character group** or **SeqFlows** node shows a context menu:

- **Create SeqFLOW...** — Create a new SeqFLOW controller for this character (see [[Creating SeqFLOWs]])
- **Kit Setup Info...** — Shows a guide for setting up a character kit to use these controllers (see [[Kit Setup]])

## Clicking Nodes

| Click target | What happens |
|---|---|
| Character group | Loads all nodes for that character on the graph |
| SeqFLOW controller | Loads that chain's flowchart on the graph |
| Individual node | Selects and highlights the node on the graph |
| [Other] folder | Loads non-SeqFLOW nodes on the graph |

Clicking any controller or group will automatically switch from Property View to Graph View.
