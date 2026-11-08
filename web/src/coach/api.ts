import { api, q } from '../api'
import { t } from '../i18n'

/** Coach (docs/dmcoach-konzept.md): Typen und Aufrufe von /api/coach. */

export type Meal = 'breakfast' | 'lunch' | 'dinner' | 'snack'
export const MEALS: Meal[] = ['breakfast', 'lunch', 'dinner', 'snack']
export const mealName = (m: Meal) => ({ breakfast: t('Frühstück'), lunch: t('Mittagessen'), dinner: t('Abendessen'), snack: t('Snacks') })[m]
/** Mahlzeit nach Uhrzeit vorschlagen. */
/** Frühstück 5–10 Uhr, Mittagessen 11:30–14:30, Abendessen 17:30–21 Uhr, dazwischen Snack (wie in der App). */
// Je Mahlzeit eine Farbe (wie in der App)
export const MEAL_COLOR: Record<Meal, string> = { breakfast: '#F2B53C', lunch: '#3CC4A0', dinner: '#F06B5B', snack: '#C239B3' }
export const mealNow = (): Meal => {
  const d = new Date(), t = d.getHours() * 60 + d.getMinutes()
  return t >= 300 && t < 600 ? 'breakfast' : t >= 690 && t < 870 ? 'lunch' : t >= 1050 && t < 1260 ? 'dinner' : 'snack'
}

export interface Nutrients { kcal: number; protein: number; fat: number; carbs: number; fiber?: number | null; sugar?: number | null; salt?: number | null; satFat?: number | null }
export interface Portion { label: string; grams: number }
export interface OffInfo { nutri: string | null; eco: string | null; nova: number | null; flags: string[]; ingredients?: string | null; allergens?: string[] | null }
export interface Food { id: string; name: string; brand: string | null; source: 'bls' | 'off' | 'own' | 'recipe'; per100: Nutrients; portions: Portion[]; liquid: boolean; barcode: string | null; quantity: string | null; info?: OffInfo | null }
export interface Hit { id: string; name: string; brand: string | null; source: string; kcalPer100: number; amount: number | null; portion: string | null; mine: boolean; image?: string | null; nutri?: string | null }

export interface Profile {
  birth: string | null; sex: 'f' | 'm' | 'd' | null; heightCm: number | null; weightKg: number | null; pal: number
  goal: 'keep' | 'lose' | 'gain' | 'muscle'; pace: number; diet: Diet; allergens: string[]; intolerances: string[]; dislikes: string[]
  pregnant: boolean; stepMode: 'fixed' | 'dynamic'; stepGoal: number; waterMl: number | null; hideNumbers: boolean; aiData: boolean
  nudges: boolean; nudgeWater: boolean; nudgeMove: boolean; nudgeWeek: boolean; waterSizes: number[]
}
export type Diet = 'dge' | 'med' | 'vegetarian' | 'vegan' | 'protein' | 'lowcarb' | 'keto'
export interface Targets {
  bmr: number | null; tdee: number | null; kcal: number | null; protein: number | null; carbs: number | null; fat: number | null
  fiber: number; waterMl: number; age: number | null; bmi: number | null; goal: string; diet: string; notes: string[]; complete: boolean
}
export interface Entry {
  id: number; day: string; meal: Meal; food: string | null; name: string; brand: string | null; amount: number | null; portion: string | null
  kcal: number; protein: number; fat: number; carbs: number; fiber: number | null; sugar: number | null; salt: number | null; at: number
}
export interface Day {
  date: string; targets: Targets; budget: number | null; activityBonus: number
  /** Ganze aktive Energie der Uhr (Anzeige); Grundbedarf ohne Bewegung = budget − movement */
  movement: number
  eaten: { kcal: number; protein: number; carbs: number; fat: number; fiber: number }
  meals: Record<Meal, Entry[]>
  water: { ml: number; goal: number; /** davon von der Uhr (Garmin) */ watch?: number; entries: { id: number; ml: number; at: number }[]; sizes?: number[] }
  steps: { steps: number | null; goal: number; mode: string }
  /** Körperwerte der letzten 14 Tage (bis zu diesem Tag) für die Verläufe */
  trend?: TrendDay[]
  activity: { steps: number | null; activeKcal: number | null; distance: number | null; sleepMin: number | null; restingHr: number | null; source: string } | null
  weight: number | null; hideNumbers: boolean
  extra: { stress: number | null; bodyBatteryHigh: number | null; bodyBatteryLow: number | null; hrv: number | null }
  avg7: { sleepMin: number | null; restingHr: number | null; hrv: number | null }
  fasting: { running: boolean; start: string | null; goal: number; plan: string; eatFrom?: string } | null
  // Aktivitäten von der Uhr (Garmin); start: Ortszeit „2026-10-27 13:02:10“
  activities?: { id: number; name: string; type: string; start: string; durationS: number | null; distanceM: number | null; kcal: number | null }[]
}
export interface TrendDay { day: string; sleepMin: number | null; restingHr: number | null; hrv: number | null; stress: number | null; bbHigh: number | null; bbLow: number | null }
export interface DayStat { day: string; kcal: number; protein: number; carbs: number; fat: number; water: number; steps: number | null; stepGoal: number | null; weight: number | null; activeKcal: number | null; sleepMin: number | null; restingHr: number | null; entries: number }

export interface Planned { title: string; recipe: string | null; food: string | null; kcalPerServing: number | null; foodName?: string | null; portion?: string | null; guessed?: boolean }
export interface Proposal { text: string; name: string; food: Food | null; grams: number | null; portion: string | null; alternatives: Hit[]; vague: boolean }
export interface Template { id: string; name: string; meal: Meal; items: { food: string | null; name: string; brand: string | null; amount: number | null; portion: string | null }[]; kcal: number }
export interface RecipeLine { text: string; name: string | null; food: Hit | null; grams: number | null; portion: string | null; kcal: number; alternatives: Hit[]; vague: boolean }
export interface RecipeNutrition {
  syncId: string; title: string; servings: number; servingsGuessed: boolean; lines: RecipeLine[]; total: Nutrients; perServing: Nutrients; weight: number; unmatched: number
  /** Kalorien je Portion laut Rezept (Angabe in der Notiz); ohne sie schlägt der Essensplan das Rezept nicht vor */
  kcalGiven?: number | null
  /** Basisrezept aus der Auslieferung: Zubereitung, Allergene, Tipp, Hinweis */
  base?: { diet: string; meal: string; prep: number; cook: number; minutes: number; allergens: string[]; steps: string[]; tip?: string | null; hint?: string | null
    image?: string | null
    /** texts: die Zeile für 1 bis 6 Portionen („½ rote Paprikaschote (80 g)“) */
    ingredients: { name: string; grams: number; texts: string[] }[] } | null
}

export const coachApi = {
  profile: () => api.get<{ profile: Profile; targets: Targets; sources: string[] }>('/api/coach/profile'),
  saveProfile: (p: Profile) => api.put<{ profile: Profile; targets: Targets; sources: string[] }>('/api/coach/profile', p),
  day: (date: string) => api.get<Day>('/api/coach/day?' + q({ date })),
  search: (text: string, meal: Meal) => api.get<Hit[]>('/api/coach/foods/search?' + q({ q: text, meal })),
  /** Markenprodukte bei Open Food Facts nach Namen (limited: gerade zu viele Suchen) */
  searchOff: (text: string) => api.get<{ hits: Hit[]; limited: boolean }>('/api/coach/foods/search/off?' + q({ q: text })),
  foodImage: (id: string, file: File) => { const f = new FormData(); f.append('file', file); return api.form<{ image: string }>('/api/coach/foods/' + encodeURIComponent(id) + '/image', f) },
  recent: (meal: Meal) => api.get<Hit[]>('/api/coach/foods/recent?' + q({ meal })),
  frequent: () => api.get<Hit[]>('/api/coach/foods/frequent'),
  photoAvailable: () => api.get<{ available: boolean }>('/api/coach/photo'),
  photo: (image: Blob) => api.putBlob<{ text: string; items: Proposal[] }>('/api/coach/photo', image),
  food: (id: string) => api.get<Food>('/api/coach/foods/' + encodeURIComponent(id)),
  barcode: (code: string) => api.get<Food>('/api/coach/foods/barcode/' + encodeURIComponent(code)),
  saveFood: (body: { name: string; brand?: string | null; barcode?: string | null; per100: Nutrients; portions: Portion[]; liquid: boolean }) => api.post<Food>('/api/coach/foods', body),
  add: (body: { date: string; meal: Meal; food?: string; amount?: number; portion?: string | null; name?: string; quick?: Nutrients; remember?: string }) => api.post<Entry>('/api/coach/diary', body),
  change: (id: number, body: { amount?: number; meal?: Meal; portion?: string | null }) => api.put<Entry>('/api/coach/diary/' + id, body),
  remove: (id: number) => api.del('/api/coach/diary/' + id),
  water: (date: string, ml: number) => api.post('/api/coach/water', { date, ml }),
  removeWater: (id: number) => api.del('/api/coach/water/' + id),
  weight: (date: string, weight: number | null) => api.put('/api/coach/body', { date, weight }),
  steps: (date: string, steps: number) => api.post('/api/coach/activity', { source: 'manual', days: [{ date, steps }] }),
  stats: (days: number) => api.get<{ days: DayStat[]; targets: Targets }>('/api/coach/stats?' + q({ days })),
  deleteAll: () => api.del('/api/coach/data'),
  planned: (date: string, meal: Meal) => api.get<Planned[]>('/api/coach/planned?' + q({ date, meal })),
  parse: (text: string, meal: Meal) => api.post<Proposal[]>('/api/coach/parse', { text, meal }),
  foodmap: (name: string, food: string) => api.put('/api/coach/foodmap', { name, food }),
  templates: () => api.get<Template[]>('/api/coach/templates'),
  saveTemplate: (name: string, date: string, meal: Meal) => api.post<Template>('/api/coach/templates', { name, date, meal }),
  applyTemplate: (id: string, date: string, meal: Meal) => api.post<{ added: number }>('/api/coach/templates/' + encodeURIComponent(id) + '/apply', { date, meal }),
  deleteTemplate: (id: string) => api.del('/api/coach/templates/' + encodeURIComponent(id)),
  /** ids: nur diese Einträge (Auswahl bei „Wie gestern“) */
  copy: (from: string, to: string, meal: Meal, toMeal: Meal, ids?: number[]) => api.post<{ added: number }>('/api/coach/diary/copy', { from, to, meal, toMeal, ids }),
  recipe: (syncId: string) => api.get<RecipeNutrition>('/api/coach/recipes/' + encodeURIComponent(syncId)),
  recipeServings: (syncId: string, servings: number) => api.put<RecipeNutrition>('/api/coach/recipes/' + encodeURIComponent(syncId) + '/servings', { servings }),
  adoptRecipe: (syncId: string) => api.post<{ id: number; syncId: string }>('/api/coach/recipes/' + encodeURIComponent(syncId) + '/adopt', {}),
}

/** yyyy-MM-dd in lokaler Zeit. */
export const isoDay = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
export const addDays = (day: string, n: number) => { const d = new Date(day + 'T12:00:00'); d.setDate(d.getDate() + n); return isoDay(d) }
export const fmt = (n: number | null | undefined, digits = 0) => n == null ? '–' : n.toLocaleString(undefined, { maximumFractionDigits: digits, minimumFractionDigits: 0 })

// Schrittziel erreicht: Balken dunkelgrün (Tagesansicht und Übersicht)
export const STEPS_DONE = '#1E7B3E'
// Was über dem Schrittziel liegt
export const STEPS_EXTRA = '#9BE07A'
