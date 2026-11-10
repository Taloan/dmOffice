// Englische Texte, nach Bereichen getrennt. Schluessel ist der deutsche Text.
import { app } from './app'
import { chat } from './chat'
import { common } from './common'
import { mail } from './mail'
import { menus } from './menus'
import { pim } from './pim'
import { search } from './search'
import { server } from './server'
import { update } from './update'
import { dashboard } from './dashboard'
import { audio } from './audio'
import { imports } from './imports'
import { tresor } from './tresor'
import { dms } from './dms'
import { recipescan } from './recipescan'
import { plainbackup } from './plainbackup'

export const en: Record<string, string> = Object.assign({}, chat, common, app, mail, pim, server, menus, search, update, dashboard, audio, tresor, imports, dms, recipescan, plainbackup)
