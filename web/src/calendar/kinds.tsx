import type { ComponentType } from 'react'
import { Food16Regular, Food20Regular, CalendarLtr16Regular } from '@fluentui/react-icons'
import type { FluentIcon } from '@fluentui/react-icons'
import { Collection } from '../api'
import { t } from '../i18n'
import { MealPlan } from './MealPlan'

/** Was eine Planungsansicht von der Kalenderansicht bekommt: ihre eingeschalteten Kalender, wie viele es von der Art gibt, und "Kalender dieser Art anlegen". */
export interface PlanProps { calendars: Collection[]; total: number; onCreate: () => void }

/**
 * Register der Kalenderarten (Server: CalendarKinds.cs). Eine Art beschreibt ihre Gruppe in der Kalenderliste (Titel, Symbol)
 * und, wenn sie eine hat, ihre eigene Planungsansicht (Menueband-Knopf und Komponente). Kalender einer Art mit Planungsansicht
 * erscheinen nur dort, normale nur in den normalen Ansichten. Eine neue Art: hier eintragen und im Server in CalendarKinds.All.
 */
export interface CalendarKind {
  key: string
  /** Name im Dialog */
  label: () => string
  hint: () => string | undefined
  /** Gruppe in der linken Liste */
  group: () => string
  groupIcon: FluentIcon
  /** Eigene Planungsansicht */
  plan?: { label: () => string; icon: FluentIcon; view: ComponentType<PlanProps> }
}

export const CALENDAR_KINDS: CalendarKind[] = [
  { key: 'normal', label: () => t('Normaler Kalender'), hint: () => undefined, group: () => t('Meine Kalender'), groupIcon: CalendarLtr16Regular },
  {
    key: 'meal', label: () => t('Essensplan'), hint: () => t('Erscheint in der Ansicht „Essensplan“, nicht in den normalen Kalenderansichten.'),
    group: () => t('Essensplan'), groupIcon: Food16Regular,
    plan: { label: () => t('Essensplan'), icon: Food20Regular, view: MealPlan },
  },
]

export const kindOf = (c: Collection): CalendarKind => CALENDAR_KINDS.find(k => k.key === c.calendarKind) ?? CALENDAR_KINDS[0]
/** Kalender, die die normalen Ansichten (Tag, Woche, Monat ...) zeigen */
export const inNormalViews = (c: Collection) => !kindOf(c).plan
