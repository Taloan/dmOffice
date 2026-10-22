// Englische Texte, nach Bereichen getrennt. Schluessel ist der deutsche Text.
import { app } from './app'
import { common } from './common'
import { mail } from './mail'
import { menus } from './menus'
import { pim } from './pim'
import { search } from './search'
import { server } from './server'
import { update } from './update'

export const en: Record<string, string> = Object.assign({}, common, app, mail, pim, server, menus, search, update)
