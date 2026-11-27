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
import { journal } from './journal' // [feature:journal]
import { dashboard } from './dashboard'
import { pgp } from './pgp'
import { audio } from './audio'
import { imports } from './imports'
import { tresor } from './tresor'
import { dms } from './dms'
import { recipescan } from './recipescan'
import { plainbackup } from './plainbackup'
import { plugins } from './plugins'
import { gallerytile } from './gallerytile'
import { mailsnippets } from './mailsnippets' // [feature:mailsnippets]
import { savemode } from './savemode' // [feature:savemode]
import { photoexport } from './photoexport' // [feature:photoexport]
import { mailaccountoff } from './mailaccountoff' // [feature:mailaccountoff]
import { junklock } from './junklock' // [feature:junklock]
import { rulecopyread } from './rulecopyread' // [feature:rulecopyread]
import { galmedium } from './galmedium' // [feature:galmedium]
import { mailtocolumn } from './mailtocolumn' // [feature:mailtocolumn]
import { catchalldomains } from './catchalldomains' // [feature:catchalldomains]
import { mailsignatures } from './mailsignatures' // [feature:mailsignatures]
import { mailavatarfilter } from './mailavatarfilter' // [feature:mailavatarfilter]
import { mailthreads } from './mailthreads' // [feature:mailthreads]
import { mailaireply } from './mailaireply' // [feature:mailaireply]
import { gatekeeper } from './gatekeeper' // [feature:gatekeeper]
import { proxyconfirm } from './proxyconfirm' // [feature:proxyconfirm]
import { publicurl } from './publicurl' // [feature:publicurl]
import { lenscorr } from './lenscorr' // [feature:lenscorr]
import { lenslib } from './lenslib' // [feature:lenslib]
import { adminclean } from './adminclean' // [feature:adminclean]
import { uploadlimit } from './uploadlimit' // [feature:uploadlimit]
import { officejwthint } from './officejwthint' // [feature:officejwthint]
import { sftptrust } from './sftptrust' // [feature:sftptrust]

export const en: Record<string, string> = Object.assign({}, chat, common, app, mail, pim, server, menus, search, update, dashboard, pgp, audio, tresor, imports, dms, recipescan, plainbackup, plugins, gallerytile,
  journal, // [feature:journal]
  mailsnippets, // [feature:mailsnippets]
  savemode, // [feature:savemode]
  photoexport, // [feature:photoexport]
  mailaccountoff, // [feature:mailaccountoff]
  junklock, // [feature:junklock]
  rulecopyread, // [feature:rulecopyread]
  galmedium, // [feature:galmedium]
  mailtocolumn, // [feature:mailtocolumn]
  catchalldomains, // [feature:catchalldomains]
  mailsignatures, // [feature:mailsignatures]
  mailavatarfilter, // [feature:mailavatarfilter]
  mailthreads, // [feature:mailthreads]
  mailaireply, // [feature:mailaireply]
  gatekeeper, // [feature:gatekeeper]
  proxyconfirm, // [feature:proxyconfirm]
  publicurl, // [feature:publicurl]
  lenscorr, // [feature:lenscorr]
  lenslib, // [feature:lenslib]
  uploadlimit, // [feature:uploadlimit]
  officejwthint, // [feature:officejwthint]
  adminclean, // [feature:adminclean]
  sftptrust, // [feature:sftptrust]
)
