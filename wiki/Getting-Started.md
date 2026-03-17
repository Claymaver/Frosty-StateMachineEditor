# Getting Started

## Installation

1. Download the latest release `.dll` from the [Releases page](../../releases)
2. Place the `.dll` in your Frosty Editor's `Plugins` folder
3. Launch Frosty Editor

## First Launch

1. Open a SWBF2 project in Frosty Editor
2. In the Data Explorer, navigate to a state machine asset:
   - `Gameplay/SoldierStateMachine/DefaultSoldierStateMachine` (main file — all heroes and reinforcements)
   - `Gameplay/SoldierStateMachine/DroidekaStateMachine`
3. Double-click to open it

The editor will parse the asset and display two things:
- **Left panel**: Tree explorer with all characters grouped alphabetically
- **Right panel**: Property view (standard Frosty EBX editor) by default

## Switching Views

Use the **Graph** / **Properties** button in the top-right of the explorer panel to toggle between:

- **Property View** (default) — Full EBX property grid, same as Frosty's built-in editor. Use this for raw data access.
- **Graph View** — Visual flowchart of state chains. Activates automatically when you click a SeqFLOW controller in the tree.

## Understanding the Tree

Characters are grouped by their naming prefix:

```
Anakin (3)              ← Character group (3 controllers)
  ├─ SeqFlows (2)       ← SeqFLOW-type controllers
  │   ├─ Attack [SeqFLOW] (16)   ← 16 nodes in this chain
  │   └─ Frenzy [SeqFLOW] (8)
  └─ [Other] (1)        ← Non-SeqFLOW controllers
      └─ Conduit Track
```

- Click a **character group** to load all its nodes on the graph
- Click a **SeqFLOW controller** to load that specific chain
- Click an **individual node** to select and highlight it

## Default Sort

The tree sorts by **Category then Name** by default. You can change this with the sort dropdown above the tree.

## Next Steps

- [[Graph View]] — Learn about the visual flowchart
- [[Creating SeqFLOWs]] — Make controllers for custom characters
- [[Adding and Removing Nodes]] — Edit attack chains
