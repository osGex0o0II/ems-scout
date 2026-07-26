# Navigation Labels Design

## Scope

Update only the visible labels in the WinUI navigation shell.

## Required Labels

The navigation remains in its existing primary and footer containers. Its visible labels are:

1. 概览
2. 采集
3. 数据
4. 区域
5. 审计
6. 设置
7. 诊断

## Preserved Behavior

- Keep the existing menu placement, icons, and navigation targets.
- Keep the internal tags `home`, `collection`, `devices`, `rules`, `audit`, `settings`, and `diagnostics` unchanged.
- Do not change page titles, commands, or other references to 数据管理, 区域组, or 系统设置.

## Verification

Update the navigation information-architecture test to assert the new labels and existing tags, then run that focused test.
