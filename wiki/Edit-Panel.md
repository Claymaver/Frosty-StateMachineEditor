# Edit Panel

The edit panel appears on the right side of the graph view when a node is selected. It provides detailed information and editing capabilities through four tabs.

## Overview Tab

The overview tab shows the node's identity and key properties.

- **Name** — The node's display name, editable. Changes are reflected in the tree and graph.
- **Category** — The detected category (Lightsaber, Force, Movement, etc.) based on naming patterns
- **Sequence Position** — Where this node falls in the chain order
- **Copy from...** — Opens a picker to copy this node's configuration from another character's version of the same action. Useful for quickly cloning setups across heroes.

## Transitions Tab

Shows all outgoing transitions from the selected node.

Each transition entry displays:
- **Target node** — Which node this transition leads to
- **Condition type** — What triggers the transition
- **Child conditions** — Any nested conditions that must be met

This is a read-only view of the transition structure. To modify transitions, use the wire drag-and-drop on the graph or edit via the Raw tab.

## Details Tab

A deep scan of the node's internal structure, showing:

- **Clips** — Animation clip references (`CharacterStateClipControllerData`)
- **Blends** — Blend controllers for animation blending
- **Conditions** — Transition conditions and their parameters
- **Substates** — Any nested state machine structures

This tab is useful for understanding what animations and logic a node contains without diving into raw EBX properties.

## Raw Tab

Direct access to the underlying EBX object via a property grid. This shows every field on the node's `CharacterStateStateFlowNodeControllerData` object.

Use this for:
- Editing specific fields not exposed in other tabs
- Debugging unexpected behavior
- Advanced modding that needs precise control over EBX values
