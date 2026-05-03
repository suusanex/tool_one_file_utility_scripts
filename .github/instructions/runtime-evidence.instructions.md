---
name: Runtime Evidence Format
description: Enforce sequences-first PlantUML + extracted Ledger for runtime-evidence.md
applyTo: "plans/*-runtime-evidence.md"
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: CC-BY-4.0
# License: https://creativecommons.org/licenses/by/4.0/
# Source: https://github.com/suusanex/coding_agent_plan_and_verify_process
---

# MUST (completion criteria)
- The document is incomplete unless it contains:
  (1) "Scenario Sections" with PlantUML sequences for every scenario
  (2) A "Scenario Ledger" table extracted from those sequences
- Do NOT output C4 Context/Container diagrams.
- PlantUMLの as <alias> は - を含めない（_ に置換）。表示名には元のC4 IDを含めてよい。

# REQUIRED structure
1) Scenario Sections (repeat per scenario)
- Heading: "### Scenario S-XXX: <title>"
- Subheading: "#### Sequence (PlantUML)"
- A PlantUML code block: starts "@startuml" and ends "@enduml"
- Subheading: "#### Component–Step Map" table

2) Scenario Ledger (AFTER sequences)
- A table listing scenarios and linking to their sections.
