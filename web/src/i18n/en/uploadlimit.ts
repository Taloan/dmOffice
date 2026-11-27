// Obergrenze je Hochlade-Link (Kennung uploadlimit, 2.2; web/src/admin/AdminView.tsx, DmOffice.Server/Dav/LinkUploadLimit.cs)
export const uploadlimit: Record<string, string> = {
  'Hochladen höchstens je Link': 'Upload limit per link',
  '„Hochladen höchstens je Link“ gilt für alles, was über einen einzelnen Hochlade-Link zusammen hochgeladen wird, sofort für alle Links. Das Kontingent des Besitzers gilt zusätzlich.':
    '“Upload limit per link” covers everything uploaded through a single upload link in total and applies to all links right away. The owner’s quota applies as well.',
  'Über diesen Link lässt sich nichts mehr hochladen – die Obergrenze für diesen Link ist erreicht.':
    'Nothing more can be uploaded through this link – the limit for this link has been reached.',
}
