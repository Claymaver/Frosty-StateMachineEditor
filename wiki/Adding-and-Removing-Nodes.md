# Adding and Removing Nodes

The chain editing tools let you add nodes from other characters, remove nodes, and commit your changes to the state machine.

## Adding Nodes

1. Load a SeqFLOW controller in the graph (click it in the tree)
2. Click **Add Node** in the toolbar
3. A picker dialog appears showing all available nodes from all characters
4. Select a node to copy (e.g., Luke's "Swing 3" attack node)
5. The node is **deep-copied** into your controller with new GUIDs

### What Gets Copied

When you add a node, the entire node structure is duplicated:
- The node controller itself
- All transition conditions
- Clip controllers and animation references
- Keyframed channel controllers
- All child objects

Every copied object is registered in `AllControllerDatas` with a correct `AssetIndex` value, which is required for the game to load the state machine without crashing.

### Cross-Hero Copying

You can copy nodes from any character to any other character. For example, you can take Anakin's attack swing and add it to a custom character's chain. The animations referenced by the node will still point to the original character's animations — you'll need to update those references separately if you want different animations.

## Removing Nodes

1. Select a node on the graph (click it)
2. Click **Remove Node** in the toolbar
3. The node is removed from the chain

## Committing Changes

After adding or removing nodes, click **Commit Chain** to finalize:

- Transitions are rewired to maintain the chain sequence
- The controller's `Subjects` list is rebuilt
- The asset is marked as modified

**Important**: Changes aren't saved to disk until you save the project in Frosty Editor (Ctrl+S).

## Edit Panel

When a node is selected, the edit panel on the right shows four tabs:

### Overview Tab
- **Name** — Editable display name for the node
- **Properties** — Key node properties
- **Copy from...** — Copy this node's configuration from another character's equivalent node

### Transitions Tab
- Shows all outgoing transitions from this node
- Each transition shows its target node and conditions

### Details Tab
- Deep scan results: clips, blends, conditions, substates
- Shows animation references and nested controller data

### Raw Tab
- Direct access to the underlying EBX object properties
- For advanced users who need to edit specific fields
