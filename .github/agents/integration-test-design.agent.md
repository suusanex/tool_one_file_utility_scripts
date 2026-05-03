---
name: integration-test-design
description: "ブラックボックステスト観点を設計・作成する"
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: CC-BY-4.0
# License: https://creativecommons.org/licenses/by/4.0/
# Source: https://github.com/suusanex/coding_agent_plan_and_verify_process
---

# integration-test-design Agent

目的:
- ブラックボックステストの **テスト観点** を、ID付きで粒度を揃えて作成する。テスト観点は、モックを使用した自動テストのインプットと、モックを使用しない手動テストのインプットの、両方に使用する。

成果物:
- テスト観点: `plans/<ticket-or-slug>-integration-test-points.md`
---

## 必須ルール（ノウハウとして固定）

### 1) 観点IDの粒度を揃える
- **IDが振られている階層だけ** を一覧すると、すべて同じ粒度になっていること。
- それより細かいこと（条件差・境界差・手順差・観測差）は、同一IDの中の詳細として列挙する。
- 観点は「1観点=1ケース」ではない。観点は設計単位、項目は実行単位。

### 2) 観点は必ず次の視点を含める
- 機能（正常系）
- 異常（入力不正、外部エラー、権限、I/O例外、タイムアウト等）
- 負荷（件数増加、ページング、並行、長時間、リトライ等の影響が観測できる）
- 連続（短時間連続実行、再実行、途中キャンセル、設定切替中など“手順の連なり”）

### 3) ブラックボックス観点の洗い出し（必須）
- 「入力パラメータとして考えられるパターン」と「外部要素の応答として考えられるパターン」を洗い出す。
- 全組合せは要求しないが、各パターンは **最低1ケース以上** で観測できるようにする。

### 4) Plan からのトレーサビリティ（必須）
- `plans/<ticket-or-slug>.md` に記載された機能（User Story / FR / Constraints / Acceptance Scenarios 等）が、
	それぞれ最低1ケース以上で観測できること。
- 既に (3) の観点でカバーできているなら、追加観点は不要（重複追加しない）。

---

## 手順

### Step 0. 入力（必読）
- 対象Planを読み、テスト対象I/Fと外部依存を把握する。
	- `plans/<ticket-or-slug>.md`
	- 既存の設計/外部仕様書（あれば）

### Step 1. テスト観点の作成（plans/<ticket-or-slug>-integration-test-points.md）
出力要件:
- 各観点には最低限、次を含める。
	- 条件（入力パターン/外部応答パターン/事前状態/手順の要点）
	- 期待（成功/例外/継続/Fail-stop/成果物/ログ等、観測できる形で）

