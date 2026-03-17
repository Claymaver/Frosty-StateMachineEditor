# Kit Setup

This page explains how state machine controllers connect to playable characters through the kit system.

## How Characters Use State Machines

All hero and reinforcement characters in SWBF2 share a single state machine file — typically `DefaultSoldierStateMachine.ebx`. Characters are linked to their specific controllers through **naming convention**, not explicit assignment.

### The Naming Link

A controller named `A.Anakin.Attack.SeqFLOW` is automatically associated with Anakin because the game matches the character name in the controller's name to the character's kit configuration.

This means:
- Creating a controller named `A.MyHero.Attack.SeqFLOW` automatically makes it available to a character named "MyHero"
- No changes are needed in the state machine file itself to "assign" controllers
- The name is the assignment

## Accessing Kit Setup Info

1. Right-click a character group in the tree explorer
2. Select **Kit Setup Info...**
3. A window shows:
   - All SeqFLOW controllers for that character
   - Other controllers
   - Step-by-step instructions for kit setup
   - A **Copy to Clipboard** button to save the info

## Setting Up a New Character Kit

To make a completely new character playable with custom SeqFLOWs:

### Step 1: Create SeqFLOWs
Use the [[Creating SeqFLOWs]] workflow to build attack chains and ability sequences for your character.

### Step 2: Duplicate an Existing Kit
In Frosty Editor's Data Explorer:

1. Find an existing hero kit, e.g.:
   ```
   Gameplay/Kits/Hero/Anakin/Kits/Kit_Hero_Anakin
   ```
2. Duplicate it and rename for your character

### Step 3: Update Kit References

The kit EBX contains several key fields:

- **CharacterStateChannelValues** — References to `PublicChannels` from the state machine. These must point to channels in the same `SoldierStateMachine` asset your SeqFLOWs live in.
- **Blueprint → LocoAnimatable** — GUID reference that links the kit to the state machine asset file.

### Step 4: Verify in Game

Launch the game with your mod to verify:
- The character loads without crashes
- Attack chains play correctly
- Animations trigger as expected

## Common Issues

| Issue | Cause | Fix |
|---|---|---|
| Game crashes on character load | Missing AllControllerDatas registration | Ensure you created SeqFLOWs through the plugin (it handles registration automatically) |
| Character has no attacks | Controller name doesn't match kit | Verify naming convention matches: `{Prefix}.{Character}.{Action}.SeqFLOW` |
| Animations don't play | Clip references point to wrong animations | Update clip references in the node's Details/Raw tab |
| Missing sounds | Conduit track not wired to all nodes | Known issue — conduit may need manual transition wiring |
