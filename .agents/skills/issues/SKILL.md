---
name: issues
description: Создает github issues и milestones из файла плана. Используй, когда есть готовый план с фазами и нужно создать бэклог на github.
---

# Issue Generator

Прочитай план из файла: $ARGUMENTS

Для каждой фазы создай milestone и issues в Github, используя gh CLI.

## Порядок действий

1. Прочитай файл плана
2. Для каждой фазы создай milestone:
   `gh api repos/:owner/:repo/milestones -f title="Фаза N: название"`
3. Для каждой задачи в фазе создай Issue:
   `gh issue create --title "..." --body "..." --label "..." --milestone "..."`

## Формат Issue

**Title:** текст задачи из плана (без [])
**Body:** описание задачи
