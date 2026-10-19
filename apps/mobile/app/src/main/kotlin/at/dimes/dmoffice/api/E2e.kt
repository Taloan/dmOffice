package at.dimes.dmoffice.api

import android.util.Base64
import at.dimes.dmoffice.data.Account
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.PublicKey
import java.security.SecureRandom
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import java.security.spec.PKCS8EncodedKeySpec
import java.util.concurrent.atomic.AtomicLong
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Verschluesselter Umschlag zur Web-API und zu DAV (Gegenstueck zu E2e.cs im Server):
 * ECDH P-256 zwischen Geraeteschluessel und Serverschluessel, HKDF-SHA256, AES-256-GCM.
 * Proxy und Netz sehen nur Chiffrat; entschluesselt wird erst in dmOffice.
 */
object E2e {
    const val VERSION = "dmoffice-e2e-v1"

    fun b64u(b: ByteArray): String = Base64.encodeToString(b, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)
    fun unb64u(s: String): ByteArray = Base64.decode(s, Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING)

    fun generateKeyPair(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()

    /** Oeffentlicher Schluessel als roher Punkt 0x04 || X || Y (65 Byte). */
    fun exportPublic(pub: PublicKey): String {
        val p = (pub as ECPublicKey).w
        return b64u(byteArrayOf(4) + pad(p.affineX) + pad(p.affineY))
    }

    private fun pad(n: BigInteger): ByteArray {
        val b = n.toByteArray()
        return if (b.size == 32) b else if (b.size > 32) b.copyOfRange(b.size - 32, b.size) else ByteArray(32 - b.size) + b
    }

    fun importPublic(raw: String): PublicKey {
        val bytes = unb64u(raw)
        require(bytes.size == 65 && bytes[0] == 4.toByte()) { "Ungueltiger Serverschluessel" }
        val params = (generateKeyPair().public as ECPublicKey).params
        val point = ECPoint(BigInteger(1, bytes.copyOfRange(1, 33)), BigInteger(1, bytes.copyOfRange(33, 65)))
        return KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(point, params))
    }

    fun importPrivate(pkcs8: String): PrivateKey = KeyFactory.getInstance("EC").generatePrivate(PKCS8EncodedKeySpec(unb64u(pkcs8)))

    fun deriveKey(priv: PrivateKey, serverPub: PublicKey, devicePub: String, serverPubRaw: String): ByteArray {
        val ka = KeyAgreement.getInstance("ECDH").apply { init(priv); doPhase(serverPub, true) }
        val shared = ka.generateSecret()
        return hkdf(shared, unb64u(devicePub) + unb64u(serverPubRaw), VERSION.toByteArray(), 32)
    }

    /** HKDF-SHA256 (RFC 5869). */
    fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, length: Int): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(if (salt.isEmpty()) ByteArray(32) else salt, "HmacSHA256"))
        val prk = mac.doFinal(ikm)
        mac.init(SecretKeySpec(prk, "HmacSHA256"))
        val out = ByteArray(length)
        var t = ByteArray(0)
        var pos = 0
        var i = 1
        while (pos < length) {
            mac.reset(); mac.update(t); mac.update(info); mac.update(i.toByte())
            t = mac.doFinal()
            val n = minOf(t.size, length - pos)
            System.arraycopy(t, 0, out, pos, n); pos += n; i++
        }
        return out
    }

    fun aad(deviceId: String, counter: Long, response: Boolean): ByteArray = ("$VERSION|$deviceId|$counter" + (if (response) "|resp" else "")).toByteArray()

    fun seal(key: ByteArray, plain: ByteArray, aad: ByteArray): ByteArray {
        val nonce = ByteArray(12).also { SecureRandom().nextBytes(it) }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        c.updateAAD(aad)
        return nonce + c.doFinal(plain)
    }

    fun open(key: ByteArray, sealed: ByteArray, aad: ByteArray): ByteArray {
        require(sealed.size >= 28) { "zu kurz" }
        val c = Cipher.getInstance("AES/GCM/NoPadding")
        c.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, sealed.copyOfRange(0, 12)))
        c.updateAAD(aad)
        return c.doFinal(sealed, 12, sealed.size - 12)
    }

    /**
     * Umschlag einrichten: Serverschluessel (aus dem QR-Code, sonst vom Server), eigenes Schluesselpaar,
     * Geraet mit dem App-Passwort registrieren. Ein Server ohne /api/e2e/key bleibt bei TLS allein.
     */
    fun pair(a: Account): Account {
        val plain = ApiClient(a.copy(deviceId = null))
        val serverKey = a.serverKey ?: try { plain.get<KeyInfo>(plain.url("/api/e2e/key")).key } catch (e: ApiException) { if (e.code == 404) return a else throw e }
        val kp = generateKeyPair()
        val pub = exportPublic(kp.public)
        val reg: Registration = plain.postFor(plain.url("/api/e2e/register"), mapOf("pubKey" to pub))
        if (reg.serverKey != serverKey) throw ApiException(400, "Serverschlüssel passt nicht zum QR-Code")
        return a.copy(serverKey = serverKey, deviceId = reg.deviceId, userId = reg.userId, devicePublic = pub, devicePrivate = b64u(kp.private.encoded), counter = 0)
    }

    @Serializable private data class Inner(val method: String, val path: String, val headers: Map<String, String>, val body: String?)
    @Serializable private data class Outer(val status: Int, val headers: Map<String, String> = emptyMap(), val body: String? = null)
    @Serializable data class KeyInfo(val key: String, val version: String = "")
    @Serializable data class Registration(val deviceId: String, val userId: String, val serverKey: String, val version: String = "")

    /**
     * Zaehler des Umschlags. Muss je Geraet in der ganzen App gemeinsam sein: Kalender-Abgleich und
     * Web-API benutzen eigene Clients - mit je eigenem Zaehler vergaeben sie Nummern doppelt, und der
     * Server lehnt die zweite als Wiederholung ab (409). Prefs implementiert das, gespeichert ueber Neustarts.
     */
    interface Counters {
        fun next(deviceId: String): Long
        fun skip(deviceId: String, by: Long)
    }

    /** Nur fuer Faelle ohne Prefs (Tests): Zaehler im Speicher, ab dem Stand des Kontos. */
    class MemoryCounters(start: Long) : Counters {
        private val c = AtomicLong(start)
        override fun next(deviceId: String) = c.incrementAndGet()
        override fun skip(deviceId: String, by: Long) { c.addAndGet(by) }
    }

    /**
     * OkHttp-Interceptor: jede Anfrage an den Server wird eingepackt und als POST an
     * /api/e2e/{user}/{device} geschickt; die Antwort kommt entschluesselt zurueck, als waere
     * sie direkt gekommen. Zaehler je Geraet streng steigend (Fenster im Server: 256).
     */
    class Interceptor(private val account: Account, private val counters: Counters) : okhttp3.Interceptor {
        private val json = Json { ignoreUnknownKeys = true; explicitNulls = false }
        private val key: ByteArray = deriveKey(importPrivate(account.devicePrivate!!), importPublic(account.serverKey!!), account.devicePublic!!, account.serverKey)
        private val envelopeUrl = account.baseUrl + "/api/e2e/" + account.userId + "/" + account.deviceId

        override fun intercept(chain: okhttp3.Interceptor.Chain): Response {
            val req = chain.request()
            if (!req.url.toString().startsWith(account.baseUrl + "/")) return chain.proceed(req)
            val path = req.url.encodedPath + (req.url.encodedQuery?.let { "?$it" } ?: "")
            val headers = HashMap<String, String>()
            for (i in 0 until req.headers.size) {
                val n = req.headers.name(i)
                if (!n.equals("Authorization", true) && !n.equals("Host", true) && !n.equals("User-Agent", true)) headers[n] = req.headers.value(i)
            }
            var bodyB64: String? = null
            req.body?.let { b ->
                val buf = Buffer(); b.writeTo(buf)
                bodyB64 = Base64.encodeToString(buf.readByteArray(), Base64.NO_WRAP)
                b.contentType()?.let { headers["Content-Type"] = it.toString() }
            }
            val inner = json.encodeToString(Inner(req.method, path, headers, bodyB64)).toByteArray()
            val device = account.deviceId!!
            fun send(): Pair<Long, Response> {
                val c = counters.next(device)
                val sealed = seal(key, inner, aad(device, c, false))
                val outerReq = Request.Builder().url(envelopeUrl).header("X-DM-Counter", c.toString()).header("User-Agent", "dmOffice-Android/0.1")
                    .post(sealed.toRequestBody("application/octet-stream".toMediaType())).build()
                return c to chain.proceed(outerReq)
            }
            var (c, res) = send()
            if (res.code == 409) {
                // Zaehler hinter dem Server (z. B. nach Neuinstallation oder altem Stand): weit vorspringen, einmal neu
                res.close()
                counters.skip(device, 1000)
                val again = send(); c = again.first; res = again.second
            }
            if (res.code != 200) {
                // 404: Geraet unbekannt (App-Passwort widerrufen) -> wie 401 behandeln
                val code = if (res.code == 404) 401 else res.code
                return res.newBuilder().code(code).message("envelope ${res.code}").body(ByteArray(0).toResponseBody(null)).build()
            }
            val plain = try { open(key, res.body!!.bytes(), aad(account.deviceId, c, true)) } finally { res.close() }
            val outer = json.decodeFromString<Outer>(String(plain))
            val bytes = outer.body?.let { Base64.decode(it, Base64.NO_WRAP) } ?: ByteArray(0)
            val ct = outer.headers.entries.firstOrNull { it.key.equals("Content-Type", true) }?.value
            val b = Response.Builder().request(req).protocol(Protocol.HTTP_1_1).code(outer.status).message("e2e")
                .body(bytes.toResponseBody(ct?.toMediaType()))
            for ((k, v) in outer.headers) if (!k.equals("Content-Length", true) && !k.equals("Content-Encoding", true)) b.header(k, v)
            return b.build()
        }
    }
}
