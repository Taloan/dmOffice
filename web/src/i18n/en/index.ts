// Englische Texte, nach Bereichen getrennt. Schluessel ist der deutsche Text.
import { app } from './app'
import { common } from './common'
import { mail } from './mail'
import { pim } from './pim'
import { server } from './server'

export const en: Record<string, string> = Object.assign({}, common, app, mail, pim, server)
