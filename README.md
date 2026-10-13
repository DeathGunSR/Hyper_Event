<div align="center">

# Hyper_Event

### Real-time POS discounts & customer loyalty for the Holoo retail system

**English** · [🇮🇷 فارسی](README.fa.md)

![Platform](https://img.shields.io/badge/platform-Windows_7%E2%80%9211-0078D6?logo=windows&logoColor=white)
![C# / .NET](https://img.shields.io/badge/C%23-.NET_Framework_4.0%2B-239120?logo=csharp&logoColor=white)
![SQL Server](https://img.shields.io/badge/SQL%20Server-Holoo%20POS-CC2927?logo=microsoftsqlserver&logoColor=white)
![SQLite](https://img.shields.io/badge/SQLite-local%20store-003B57?logo=sqlite&logoColor=white)

</div>

> A Windows desktop companion for **Holoo** (an Iranian POS/ERP backed by SQL Server) that watches the till in real time, proposes the right discount the moment an invoice becomes eligible, and runs a complete customer-loyalty program — under one hard architectural guarantee:
>
> **It never writes a single row into the store's database.**

---

## Table of contents

- [1. The problem and the goal](#1-the-problem-and-the-goal)
- [2. What is in this repository](#2-what-is-in-this-repository)
- [3. Features](#3-features)
- [4. Architecture](#4-architecture)
- [5. Technology stack](#5-technology-stack)
- [6. Key engineering decisions](#6-key-engineering-decisions)
- [7. Build and run](#7-build-and-run)
- [8. Testing and verification](#8-testing-and-verification)
- [9. Roadmap — not implemented yet](#9-roadmap--not-implemented-yet)
- [10. Documentation](#10-documentation)

---

## 1. The problem and the goal

Holoo runs the point of sale for many Iranian retailers, but it ships with no mechanism for **dynamic discounts** and no **customer-loyalty** module. Store managers want two things:

1. a discount proposal at the till the moment an invoice becomes eligible, and
2. a mobile-based loyalty program (points per purchase, prize draws).

Patching an operational/accounting database is the risky way to get there: schema coupling, overwriting stored procedures, version upgrades. Hyper_Event takes the opposite approach — an **external, read-only companion layer**:

- the data layer issues **`SELECT` queries only**;
- every piece of persistent state lives in a **local SQLite file** next to the executable;
- discounts are **proposals** — entering them stays the cashier's deliberate action;
- **zero** schema changes, triggers or stored procedures on the store database.

## 2. What is in this repository

| Path | What it is |
|---|---|
| [`posdiscount/`](posdiscount/) | The Windows desktop application: source (`src/`), build scripts and full documentation — see [posdiscount/README.md](posdiscount/README.md) |
| [`explore/`](explore/) | 14 SQL investigation scripts that mapped the Holoo schema: views, financial fields, stored procedures, discount fields, settlement flags, daily-close path |
| [`out/`](out/) | Captured (UTF-8) outputs of those scripts — the raw evidence behind every design decision below |
| [`tools/`](tools/) | Small utilities, including a SQLite smoke-test harness |

## 3. Features

### Real-time invoice engine
- Polls `Res_Facture` (invoice header) joined with `Res_Fac_List` (lines) at a configurable interval (**1.5 s** by default).
- New-invoice detection keys on `F_Code`, an **`IDENTITY`** column — `WHERE F_Code > lastSeen` — because the Holoo database has `Service Broker` disabled and no snapshot isolation, so `SqlDependency`/CDC were not options.
- Only invoices of the **signed-in cashier** trigger the popup (`Res_Facture.UserCode`), and only invoices seen after start-up are considered (no startup flood).

### Discount engine
- Three modes: **percent**, **fixed** and **tiered** (percentage tiers over amount thresholds).
- Guard rails: minimum invoice amount, allowed time window, optional requirement of the cashier's Holo discount permission (`USERDB.ChangeTakhfif`), maximum percent, maximum amount, and a **per-cashier daily discount budget**.
- Final payable amount is rounded to a configured step (e.g. 1,000 Rials), in favour of the customer or the store.
- The popup appears **only for eligible invoices** and shows: total, discount (amount and percent), **payable amount after discount**, the rule that produced it, the payment methods — plus fields for the customer's mobile number and the points earned.

### Loyalty points
- Mobile capture automatically from `Res_Facture.Alias_Mobile` when present, otherwise manual entry from the popup or dashboard.
- **Iranian number normalisation**: Persian/Arabic digits, spaces, hyphens, `+98`, `0098` and leading-zero-less inputs all collapse to `09xxxxxxxxx`.
- Points rule: **every N toman spent *within one invoice* = 1 point** (integer division; separate invoices are never merged).
- Search all invoices of any number, a points leaderboard with rank/total spend, bulk lists of invoices still missing a number, and CSV export.

### Prize draws
- **1 point = 1 chance**, selection without replacement, so a customer with 9 points is nine times as likely to win as one with a single point.
- Simple (N winners) and **multi-prize** (per-prize winner counts from one shared pool) modes, optional step-by-step animated reveal.
- Participant scoping: date range, cashier, minimum invoice count, minimum points.
- Every draw is **seeded and exactly reproducible** (same seed → same winners) and is persisted locally with full winner/chance audit trails plus CSV export — the fairness story you can show to customers.

### Operations and safety
- Full settings UI backed by `settings.xml` (connection, discount rules, tiers, loyalty, pop-up behaviour, polling).
- **Self-healing connections**: detects and repairs the classic *instance-ID-in-server-name* bug (`MSSQL17.SQLEXPRESS` instead of `.\SQLEXPRESS`, the cause of error 26), probes local instances automatically and keeps a `settings.xml.bak` backup; network addresses are never rewritten.
- Logging: a daily per-invoice **CSV log** (UTF-8 with BOM, `;` separators so Persian Excel opens it correctly), plus `app.log` and `errors.log`.
- System-tray operation, single-instance guard, auto-close and sound options for the popup.

## 4. Architecture

```
┌────────────────────────  UI layer (WinForms)  ───────────────────────────┐
│ CashierSelectForm → DashboardForm (polling + daily stats)              │
│ InvoicePopupForm | SettingsForm | PointsForm | CustomerInvoicesForm     │
│ MobileEntryForm | LotteryForm                                           │
└──────────────┬───────────────────────────────────┬──────────────────────┘
               │ SELECT only                      │
        ┌──────▼───────┐                           │
        │   HolooDb    │──── SQL Server (Holoo) ───┘
        │  (read-only) │      Res_Facture / Res_Fac_List / USERDB
        └──────┬───────┘
               │
  ┌────────────┼─────────────┬────────────────┬──────────────┐
  ▼            ▼             ▼                ▼              ▼
DiscountEngine  PointsEngine  LocalStore      Lottery        CsvLog
(discount rules) (loyalty)    (local SQLite)  (seeded draw)   (CSV logs)
```

**Main flow:** `DashboardForm` polls `HolooDb.GetNewInvoices` → each new invoice runs through `DiscountEngine` and `PointsEngine` → if eligible, `InvoicePopupForm` is shown → results are recorded in `LocalStore` and the CSV log. `LotteryForm` works purely on the aggregated local data, persists every draw, and exports it.

## 5. Technology stack

| Technology | Role in the project |
|---|---|
| **C# 5 / .NET Framework 4.0+** | Application language and platform — built with Windows' in-box compiler (`csc.exe`), so it compiles and ships **without Visual Studio** (and still builds on Windows 7). |
| **Windows Forms** | Desktop UI: dashboard, popup, settings, points, lottery and the other forms. |
| **ADO.NET / `System.Data.SqlClient`** | Read-only access to the Holoo SQL Server database. |
| **SQL Server (Holoo database)** | Source of truth: `Res_Facture`, `Res_Fac_List`, `USERDB`. |
| **SQLite (`System.Data.SQLite`)** | Local store for invoices seen, mobiles, points and draws — ships `x86`/`x64` native DLLs next to the exe; nothing to install. |
| **XML (`settings.xml`)** | Human-editable configuration, always validated against a live connection before it is accepted. |
| **Jalali dates** | Taken from Holoo's own `MiladiToShamsi` SQL function — no client-side date conversion guesses. |

## 6. Key engineering decisions

1. **Polling instead of events.** `Service Broker` is disabled, `READ_COMMITTED_SNAPSHOT` is off and the compatibility level is 100 — `SqlDependency` and CDC were dead ends. Polling the `IDENTITY` key at 1–2 s intervals is the reliable path, and it is the same trick the phase-1 schema investigation validated against real data.
2. **The "no writes to Holoo" principle is enforced, not promised.** The `HolooDb` layer contains no write methods, and the self-test asserts that a real invoice's `F_Takhfif` value is byte-identical before and after the run.
3. **Discount finalisation stays with the cashier** — the app proposes, a human applies. This keeps all accounting data out of reach of a third-party tool.
4. **Per-invoice points math** — points are computed per invoice with integer division, never by summing invoices together, exactly matching the agreed business rule.
5. **Reproducible draws** — weighted sampling without replacement plus a stored `seed` makes any draw auditable after the fact.
6. **A real bug, really fixed** — an early build wrote the Windows registry *value* (`MSSQL17.SQLEXPRESS`) instead of the instance *name*, and every connection failed with error 26. `ConnectionFixer` now detects the pattern, repairs it with a backup, and refuses to save unverified server names.
7. **Non-destructive local migrations** — schema upgrades in SQLite use `ALTER TABLE ADD COLUMN` so existing points and invoices survive.

## 7. Build and run

**Prerequisites:** Windows 7 SP1 or later · .NET Framework 4.0+ · read access to the Holoo SQL Server (Windows Authentication recommended) · nothing else — SQLite is bundled.

```cmd
:: build on Windows (no Visual Studio needed)
cd posdiscount
build.cmd
```

```bash
# or from Git Bash / MSYS
cd posdiscount
sh build.sh
```

Output lands in `posdiscount/bin/`: `PosDiscount.exe`, `SelfTest.exe`, `System.Data.SQLite.dll` + `x86/` + `x64/`. **Copy the whole `bin` folder** to move the app to another machine.

```cmd
bin\PosDiscount.exe          # cashier selection → dashboard
```

```cmd
bin\SelfTest.exe --user 9    # headless test report → bin\logs\selftest.txt
bin\SelfTest.exe --draw 3     # sample prize draw over the local database
bin\SelfTest.exe --heal        # check / repair the server name in settings.xml
```

## 8. Testing and verification

`SelfTest.exe` is a headless suite whose exit code is 0 only when every check passes. It covers:

- connection, cashier list, daily totals and a `F_SumPrice = Σ(qty × price)` reconciliation against real invoices;
- the polling path and the rule *"popup only for eligible invoices"*;
- discount-engine scenarios (modes, caps, window, rounding, permission);
- the points rule corner cases (2×250k → 0 · 500k → 1 · 750k → 1 · 1M → 2 · 499,999 → 0);
- mobile-number normalisation across all supported input formats;
- the local SQLite database on a temporary file — save, search, recalculate, CSV export — plus a **migration test that proves old data survives**;
- prize draws: chance distribution (≈90 % win rate for 9× odds, 1:2:4 ratios), no duplicate winners, multi-prize mapping, and **identical results from the same seed**;
- connection self-healing against a real server, including a deliberately broken `settings.xml`;
- **the "no writes to Holoo" guarantee** (no write methods exist; a real invoice's discount field is unchanged afterwards);
- building every form and checking that labels fit and controls do not overlap, CSV logging columns, and layout screenshots (`--ui-shot`).

> **Honest testing limitation:** the real-time path was exercised against a **restored backup** of the production database (data up to 2026-02-09), not against a live store server during working hours. A true live-environment test must be run on the customer's actual server.

## 9. Roadmap — not implemented yet

> The items below are **proposals only**; none of them exists in the codebase today.

- a careful write-path for discounts into Holoo (shadow table keyed by `F_Code`, defence against `sp_Res_FactureUpdate` overwriting whole rows);
- a reconciliation report comparing till totals with `Res_DailyRep` and the daily `FACTURE` entry;
- discount scoping by product category;
- an installer and auto-update mechanism;
- multi-store deployment with a central reporting panel.

## 10. Documentation

The complete user/administrator manual — discount rules with worked examples, every settings key, the local database schema, draw rules, troubleshooting — is in **[posdiscount/README.md](posdiscount/README.md)** (Persian). The `explore/*.sql` scripts double as living documentation of the Holoo schema this project depends on.

---

<div align="center">

**More from this portfolio:**
[hyper_barcode](https://github.com/DeathGunSR/hyper_barcode) ·
[WP-Recipe](https://github.com/DeathGunSR/WP-Recipe) ·
[HolooDB_Sync](https://github.com/DeathGunSR/HolooDB_Sync)

Maintained by [@DeathGunSR](https://github.com/DeathGunSR)

</div>
