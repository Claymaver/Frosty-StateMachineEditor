# Graph View

The graph view displays state machine nodes as a visual flowchart with draggable cards and bezier wire connections.

## Node Cards

Each node is displayed as a card with:

- **Category accent bar** — Colored stripe on the left indicating the node's category (Lightsaber, Force, Movement, etc.)
- **Name** — The node's action name (e.g., "Attack", "Dash", "Block")
- **Sequence badge** — Shows the node's position in the chain (e.g., "1", "2", "3")
- **Input port** (left) — Where incoming transitions connect
- **Output port** (right) — Where outgoing transitions leave
- **Transition count** — Shows how many transitions this node has (displayed as "X values")

## Navigation

- **Pan** — Click and drag on empty canvas space
- **Zoom** — Mouse scroll wheel
- **Select node** — Click on a node card
- **Multi-select** — Shift+click to add/remove nodes from selection
- **Box select** — Click and drag on empty space to draw a selection rectangle
- **Move nodes** — Click and drag selected nodes

## Wires (Connections)

Transitions between nodes are shown as bezier curves connecting output ports to input ports.

### Creating Connections

1. Click and drag from an **output port** (right side of a node)
2. Drag to an **input port** (left side of another node)
3. Release to create the connection

### Breaking Connections

Right-click a node and select **Break Connections** from the context menu.

## Frames

Frames are visual grouping boxes drawn behind nodes:

- **Auto-generated frames** — Created automatically based on node categories when loading a controller
- **User-created frames** — Right-click the canvas and select **Create Frame** to add a custom grouping frame

## Toolbar

When a SeqFLOW controller is loaded, a toolbar appears at the top of the graph with chain editing buttons:

- **Add Node** — Add a new node to the chain (see [[Adding and Removing Nodes]])
- **Remove Node** — Remove the selected node from the chain
- **Commit Chain** — Save chain changes, rewiring transitions and rebuilding the controller's Subjects list

## Layout

The graph uses two layout modes:

- **SeqFLOW layout** — BFS-based flowchart layout that follows the chain from the conduit track outward
- **Category-column layout** — Groups nodes into columns by category for non-sequential views
