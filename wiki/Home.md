# Frosty State Machine Editor

A visual editor plugin for [Frosty Editor](https://github.com/CadeEvs/FrostyToolsuite) that lets you view and edit **CharacterStateOwnerData** assets (soldier state machines) used in Star Wars Battlefront II.

## What It Does

Instead of manually editing thousands of raw EBX properties, this plugin gives you:

- **Tree Explorer** — Browse controllers grouped by character (Anakin, Luke, Grievous, etc.) with search and sorting
- **Graph View** — Visual flowchart of SeqFLOW state chains with draggable nodes, bezier wire connections, and category-colored cards
- **Property View** — Toggle to Frosty's standard EBX property grid for full raw access
- **Node Editing** — Add, remove, and rewire nodes in attack/ability chains
- **SeqFLOW Creation** — Create new SeqFLOW controllers for custom characters from templates
- **Cross-Hero Copying** — Copy node configurations from one hero to another via "Copy from..."

## Pages

- [[Getting Started]] — Installation and first launch
- [[Tree Explorer]] — Navigating characters and controllers
- [[Graph View]] — Understanding the visual flowchart
- [[Creating SeqFLOWs]] — Making new controllers for custom characters
- [[Adding and Removing Nodes]] — Editing attack/ability chains
- [[Edit Panel]] — Overview, Transitions, Details, and Raw tabs
- [[Kit Setup]] — Linking state machines to character kits
- [[Troubleshooting]] — Common issues and solutions

## Supported Assets

This plugin activates for any EBX asset containing `CharacterStateOwnerData`, such as:

- `Gameplay/SoldierStateMachine/DefaultSoldierStateMachine` (all heroes/reinforcements)
- `Gameplay/SoldierStateMachine/DroidekaStateMachine`
- Other character-specific state machine files

## Requirements

- Frosty Editor 1.0.6.3 (Alpha)
- Star Wars Battlefront II (2017)
