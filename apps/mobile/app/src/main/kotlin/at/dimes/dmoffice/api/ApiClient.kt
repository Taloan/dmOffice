package at.dimes.dmoffice.api

import at.dimes.dmoffice.data.Account
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import okhttp3.Credentials
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.TimeUnit

class ApiException(val code: Int, message: String) : IOException(message)

/**
 * Web-API von dmOffice mit App-Passwort (Basic). Der Server laesst damit Mail, Kalender,
 * Kontakte, Notizen und Dateien zu, keine Verwaltung (Auth.AppPasswordAllowed).
 */
class ApiClient(private val account: Account, private val counters: E2e.Counters = E2e.MemoryCounters(account.counter)) {
    val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private val auth = Credentials.basic(account.login, account.token, Charsets.UTF_8)
    private val jsonType = "application/json; charset=utf-8".toMediaType()

    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(90, TimeUnit.SECONDS).writeTimeout(90, TimeUnit.SECONDS)
        .apply { if (account.encrypted) addInterceptor(E2e.Interceptor(account, counters)) }
        .build()

    fun url(path: String, vararg query: Pair<String, Any?>): String {
        val q = query.filter { it.second != null && it.second.toString().isNotEmpty() }
            .joinToString("&") { enc(it.first) + "=" + enc(it.second.toString()) }
        return account.baseUrl + path + (if (q.isEmpty()) "" else (if (path.contains('?')) "&" else "?") + q)
    }

    private fun enc(s: String) = java.net.URLEncoder.encode(s, "UTF-8").replace("+", "%20")

    private fun build(method: String, url: String, body: RequestBody?): Request =
        Request.Builder().url(url).header("Authorization", auth).header("User-Agent", "dmOffice-Android/0.1").header("Accept", "application/json")
            .method(method, body).build()

    private fun check(r: Response): Response {
        if (r.code in 200..299) return r
        val text = try { r.body?.string() ?: "" } catch (_: Exception) { "" }
        val msg = try { json.parseToJsonElement(text).jsonObject["error"]?.jsonPrimitive?.content } catch (_: Exception) { null }
        throw ApiException(r.code, msg ?: (if (r.code == 401) "401" else "HTTP ${r.code}"))
    }

    fun getText(url: String): String = http.newCall(build("GET", url, null)).execute().use { check(it).body?.string() ?: "" }
    fun getBytes(url: String): Pair<ByteArray, String?> = http.newCall(build("GET", url, null)).execute().use { r -> check(r); (r.body?.bytes() ?: ByteArray(0)) to r.header("Content-Type") }

    inline fun <reified T> get(url: String): T = json.decodeFromString(getText(url))

    fun postText(url: String, body: String?): String =
        http.newCall(build("POST", url, (body ?: "").toRequestBody(jsonType))).execute().use { check(it).body?.string() ?: "" }
    fun putText(url: String, body: String): String =
        http.newCall(build("PUT", url, body.toRequestBody(jsonType))).execute().use { check(it).body?.string() ?: "" }
    fun delete(url: String) { http.newCall(build("DELETE", url, null)).execute().use { check(it) } }

    inline fun <reified B> post(url: String, body: B): String = postText(url, json.encodeToString(body))
    inline fun <reified B, reified T> postFor(url: String, body: B): T = json.decodeFromString(post(url, body))

    /** Multipart wie das Web: Feld "json" plus Dateien "files". */
    fun postMultipart(url: String, jsonField: String, files: List<Triple<String, String, ByteArray>>): String {
        val b = MultipartBody.Builder().setType(MultipartBody.FORM).addFormDataPart("json", jsonField)
        for ((name, mime, bytes) in files) b.addFormDataPart("files", name, bytes.toRequestBody(mime.toMediaType()))
        return http.newCall(build("POST", url, b.build())).execute().use { check(it).body?.string() ?: "" }
    }

    fun obj(vararg pairs: Pair<String, JsonElement>): JsonObject = buildJsonObject { for ((k, v) in pairs) put(k, v) }
}
