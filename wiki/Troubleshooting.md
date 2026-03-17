# Troubleshooting

## Game Crashes

### Crash when loading a character with custom nodes

**Cause**: New objects weren't registered in `AllControllerDatas` with correct `AssetIndex` values.

**Fix**: Always use the plugin's built-in tools (Add Node, Create SeqFLOW) to create new content. These automatically handle registration. If you manually added objects through the raw property editor, they may be missing from `AllControllerDatas`.

### Crash after saving and reloading the project

**Cause**: Corrupted state from partial edits.

**Fix**: Try reverting to a backup. Frosty creates backups in the project folder. If the issue persists, the state machine file may need to be re-imported from vanilla.

## Missing or Broken Animations

### Attack chain doesn't play any animations

**Possible causes**:
- Clip references still point to the template character's animations
- Node transitions aren't wired correctly
- Chain wasn't committed (click **Commit Chain** after editing)

### Intermittent missing sounds (e.g., lightsaber swing sounds)

**Known issue**: This may be related to conduit track wiring. In vanilla state machines, the conduit track connects to all nodes in the chain. Custom SeqFLOWs may only have the conduit connected to the first node.

**Workaround**: Manually check conduit track transitions in the Raw tab and ensure they reference all nodes in the chain.

## Editor Issues

### Tree doesn't show my new character

The tree groups characters by the naming convention `{Prefix}.{Character}.{Action}.{Type}`. If your controller name doesn't follow this pattern, it may appear under a generic group or not be recognized.

**Fix**: Ensure controller names follow the pattern, e.g., `A.MyHero.Attack.SeqFLOW`.

### Graph is empty after clicking a controller

**Possible causes**:
- You're in Property View — click the **Graph** button to switch
- The controller has no nodes (newly created empty SeqFLOW)
- The controller type isn't a SeqFLOW (only SeqFLOW/SEQ types show the flowchart layout)

### Buttons missing from toolbar

The chain editing toolbar (Add Node, Remove Node, Commit Chain) only appears when a **SeqFLOW controller** is selected. Clicking a character group or individual node shows all nodes but doesn't activate the toolbar.

**Fix**: Click the SeqFLOW controller itself in the tree (e.g., "Attack [SeqFLOW]"), not the character group.

## Performance

### Editor is slow to load DefaultSoldierStateMachine

This is expected — `DefaultSoldierStateMachine.ebx` contains ~10,000 objects and ~4,400 entries in AllControllerDatas. Initial parsing takes a few seconds. Subsequent interactions should be fast.

### Graph feels sluggish with many nodes

Loading a character group with many controllers at once can put hundreds of nodes on the graph. For better performance, click individual SeqFLOW controllers instead of the top-level character group.

## Reporting Bugs

When reporting issues, include:
1. What you were doing when the issue occurred
2. Any error messages from Frosty's log window
3. The state machine file you were editing
4. Whether the issue happens with vanilla data or only modified data
