/**
 * Firebase-Werte aus google-services.json (Firebase-Konsole -> Projekteinstellungen -> Android-App).
 * Ohne Datei bleiben die Felder leer und Push ist aus. Kein google-services-Plugin, damit der Build
 * auch ohne die Datei durchlaeuft.
 */
fun firebaseValues(packageName: String): Map<String, String> {
    val f = file("google-services.json")
    if (!f.exists()) return emptyMap()
    @Suppress("UNCHECKED_CAST")
    val root = groovy.json.JsonSlurper().parse(f) as Map<String, Any?>
    val info = root["project_info"] as Map<String, Any?>
    val clients = root["client"] as List<Map<String, Any?>>
    val client = clients.firstOrNull { ((it["client_info"] as Map<*, *>)["android_client_info"] as Map<*, *>)["package_name"] == packageName } ?: return emptyMap()
    val appId = (client["client_info"] as Map<*, *>)["mobilesdk_app_id"] as String
    val apiKey = ((client["api_key"] as List<Map<*, *>>).first()["current_key"]) as String
    return mapOf("FCM_APP_ID" to appId, "FCM_API_KEY" to apiKey, "FCM_PROJECT_ID" to (info["project_id"] as String), "FCM_SENDER_ID" to (info["project_number"] as String))
}

fun com.android.build.api.dsl.VariantDimension.firebaseFields(packageName: String) {
    val v = firebaseValues(packageName)
    for (k in listOf("FCM_APP_ID", "FCM_API_KEY", "FCM_PROJECT_ID", "FCM_SENDER_ID")) buildConfigField("String", k, "\"" + (v[k] ?: "") + "\"")
}

/** Version aus dem Release-Tag (-PappVersion=1.1.0); versionCode steigt mit jeder Version, sonst nimmt Android kein Update an. */
val releaseVersion = (project.findProperty("appVersion") as String?)?.takeIf { it.matches(Regex("\\d+\\.\\d+\\.\\d+")) }
/** Ohne Release-Tag (eigene Builds): Version des Zweigs aus DmOffice.Server/release.txt ("1.5" -> 1.5.0) statt 0.1.0. */
val branchVersion = rootProject.file("../../DmOffice.Server/release.txt").takeIf { it.exists() }?.readText()?.trim()
    ?.takeIf { it.matches(Regex("\\d+\\.\\d+")) }?.let { "$it.0" } ?: "0.1.0"
val appVersion = releaseVersion ?: branchVersion
/** Eigene Builds zeigen dazu den Stand: "1.5.0-dev (53f15a2)". */
val gitShort = runCatching { providers.exec { commandLine("git", "rev-parse", "--short", "HEAD"); isIgnoreExitValue = true }.standardOutput.asText.get().trim() }.getOrDefault("")
val appVersionName = releaseVersion ?: (appVersion + "-dev" + (if (gitShort.isNotEmpty()) " ($gitShort)" else ""))
val appVersionCode = appVersion.split('.').map { it.toInt() }.let { (a, b, c) -> a * 10000 + b * 100 + c }.coerceAtLeast(1)

/** Test-App fuer Version 2 (Branch v2, -Pnext): eigene Paket-ID (.next) und eigener Name - laeuft neben der normalen App. */
val next = project.hasProperty("next")
val idSuffix = if (next) ".next" else ""
/** Google-Play-Build (-Pplay): ohne Selbst-Update und ohne Zugriff auf alle Dateien (src/play/AndroidManifest.xml). */
val play = project.hasProperty("play")
/**
 * Play hat eigene Versionscodes (Version x10 plus Lauf, -PplayBuild=1..9): 1.5.0 -> 105001, 1.5.1 -> 105011. So steigen sie in
 * Play immer und kollidieren nie mit den Codes der APK von GitHub (1.5.0 -> 10500).
 */
val playBuild = (project.findProperty("playBuild") as String?)?.toIntOrNull()?.coerceIn(1, 9) ?: 1

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.ksp)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "at.dimes.dmoffice"
    compileSdk = 36

    defaultConfig {
        applicationId = "at.dimes.dmoffice" + idSuffix
        minSdk = 26
        targetSdk = 36
        versionCode = if (play) appVersionCode * 10 + playBuild else appVersionCode
        versionName = appVersionName
        vectorDrawables.useSupportLibrary = true
        // Eigener Android-Kontotyp fuer Kalender und Kontakte (Abgleich in beide Richtungen)
        resValue("string", "account_type", "at.dimes.dmoffice$idSuffix.account")
        manifestPlaceholders["appLabel"] = if (next) "dmOffice 2" else "@string/app_name"
        buildConfigField("boolean", "PLAY", play.toString())
    }
    if (play) sourceSets.getByName("release").manifest.srcFile("src/play/AndroidManifest.xml")

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            resValue("string", "account_type", "at.dimes.dmoffice$idSuffix.debug.account")
            firebaseFields("at.dimes.dmoffice$idSuffix.debug")
        }
        release {
            firebaseFields("at.dimes.dmoffice$idSuffix")
            // Test-App (-Pnext) ohne R8: der langsamste Schritt im Build der Gitea; die Release-App bleibt verkleinert
            isMinifyEnabled = !next
            isShrinkResources = !next
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        isCoreLibraryDesugaringEnabled = false
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true; buildConfig = true }
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
}

ksp { arg("room.schemaLocation", "$projectDir/schemas") }

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.room.runtime)
    implementation(libs.androidx.room.ktx)
    ksp(libs.androidx.room.compiler)
    implementation(libs.androidx.work.runtime)
    // Coach: Schritte, Schlaf, Gewicht ... von der Uhr über Health Connect
    implementation(libs.androidx.health.connect)
    // Health Connect bringt Guava zur Laufzeit mit, beim Übersetzen aber nur die leere „listenablefuture“ - WorkManager-Aufrufe brauchen ListenableFuture
    implementation("com.google.guava:guava:31.1-android")
    implementation(libs.androidx.glance.appwidget)
    implementation(libs.androidx.glance.material3)
    implementation(libs.androidx.security.crypto)
    implementation(libs.okhttp)
    // Tresor auf dem Gerät entschlüsseln (Cryptomator-Format, AGPLv3 wie dmOffice; BouncyCastle darin eingebettet).
    // Guava kommt schon als Android-Fassung mit
    implementation("org.cryptomator:cryptolib:2.4.0") { exclude(group = "com.google.guava") }
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.zxing.embedded)
    // Atlas der Galerie: Karte von OpenStreetMap ohne Schlüssel
    implementation(libs.osmdroid)
    implementation(libs.kotlinx.serialization.json)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)
    implementation(libs.kotlinx.coroutines.play.services)
    // DMS: Dokumente abfotografieren mit den Play-Diensten (ohne sie der eigene Weg in ui/dms/DmsScan.kt)
    implementation(libs.mlkit.document.scanner)
    implementation(libs.sqlcipher)
    implementation(libs.androidx.sqlite)
    implementation(libs.androidx.biometric)
    implementation(libs.androidx.fragment)
    implementation(libs.androidx.webkit)
    // Audiothek (Version 2): Wiedergabe im Hintergrund, Benachrichtigung, Sperrbildschirm, Bluetooth-Tasten (Apache-2.0)
    implementation(libs.media3.exoplayer)
    implementation(libs.media3.session)
    testImplementation(libs.junit)
    testImplementation(libs.kxml2)
    debugImplementation(libs.androidx.compose.ui.tooling)
}

/**
 * Open-Source-Bibliotheken der App (Einstellungen → Über → Open-Source-Bibliotheken): alle Module des Release-Classpath mit
 * Name, Projektseite und Lizenz aus ihrer POM (oder der Eltern-POM) im Gradle-Cache - ohne Netzwerk, die POMs lädt Gradle beim
 * Auflösen ohnehin. Lizenztexte aus tools/lizenzen/texte. Ergebnis src/main/assets/third-party.json, dasselbe Format wie
 * web/public/third-party.json; tools/lizenzen/erzeugen.mjs übernimmt es in die Gesamtliste. Läuft vor jedem Bau.
 */
abstract class OssLizenzen : DefaultTask() {
    @get:Input abstract val module: ListProperty<String>
    @get:Input abstract val cache: Property<String>
    @get:InputDirectory @get:PathSensitive(PathSensitivity.RELATIVE) abstract val texte: DirectoryProperty
    /** tools/lizenzen/zusatz.json: „overrides“ für Module ohne verwertbare Lizenzangabe in der POM (Schlüssel group:artifact). */
    @get:InputFile @get:PathSensitive(PathSensitivity.RELATIVE) abstract val zusatz: RegularFileProperty
    @get:OutputFile abstract val ziel: RegularFileProperty

    private class Pom(val name: String?, val url: String?, val licenses: List<Pair<String, String?>>, val parent: String?)

    private fun pom(coord: String): Pom? {
        val (g, a, v) = coord.split(':')
        val dir = File(cache.get(), "$g/$a/$v")
        val f = dir.listFiles()?.flatMap { it.listFiles()?.toList() ?: emptyList() }?.firstOrNull { it.name == "$a-$v.pom" } ?: return null
        val doc = javax.xml.parsers.DocumentBuilderFactory.newInstance().newDocumentBuilder().parse(f).documentElement
        fun child(e: org.w3c.dom.Element, tag: String) = (0 until e.childNodes.length).map { e.childNodes.item(it) }
            .firstOrNull { it is org.w3c.dom.Element && it.tagName == tag } as org.w3c.dom.Element?
        val lic = child(doc, "licenses")?.getElementsByTagName("license")?.let { l -> (0 until l.length).map { l.item(it) as org.w3c.dom.Element } }
            ?.map { (child(it, "name")?.textContent?.trim() ?: "") to child(it, "url")?.textContent?.trim() } ?: emptyList()
        val parent = child(doc, "parent")?.let { p -> listOf("groupId", "artifactId", "version").joinToString(":") { child(p, it)?.textContent?.trim() ?: "" } }
        return Pom(child(doc, "name")?.textContent?.trim(), child(doc, "url")?.textContent?.trim(), lic, parent)
    }

    /** Lizenzname der POM als SPDX-Kennung, soweit eindeutig; sonst der Name selbst. */
    private fun spdx(name: String, url: String?): String {
        val n = name.lowercase(); val u = url?.lowercase() ?: ""
        return when {
            "apache" in n && "2" in n || "apache.org/licenses/license-2.0" in u -> "Apache-2.0"
            n == "mit" || "mit license" in n || "opensource.org/licenses/mit" in u -> "MIT"
            "bsd" in n && ("3" in n || "new" in n || "revised" in n) || "bsd-3-clause" in u -> "BSD-3-Clause"
            "bsd" in n && ("2" in n || "simplified" in n) || "bsd-2-clause" in u -> "BSD-2-Clause"
            "eclipse public license" in n && "1" in n -> "EPL-1.0"
            "eclipse public license" in n && "2" in n -> "EPL-2.0"
            else -> name
        }
    }

    @TaskAction fun run() {
        val texts = sortedMapOf<String, String>()
        @Suppress("UNCHECKED_CAST")
        val overrides = ((groovy.json.JsonSlurper().parse(zusatz.get().asFile) as Map<String, Any?>)["overrides"] as Map<String, Map<String, Any?>>? ?: emptyMap())
            .mapValues { it.value["license"] as String }
        val pkgs = module.get().sorted().map { coord ->
            var p = pom(coord)
            var name = p?.name; var url = p?.url; var lic = p?.licenses ?: emptyList()
            // Angaben fehlen oft in der POM selbst und stehen in der Eltern-POM
            while (p?.parent != null && (lic.isEmpty() || url == null)) {
                p = pom(p.parent!!)
                if (lic.isEmpty()) lic = p?.licenses ?: emptyList()
                if (url == null) url = p?.url
            }
            val (g0, a0) = coord.split(':')
            // Override mit „ A AND B “: mehrere Lizenzen (etwa eingebettete Bibliotheken), je mit Lizenztext
            val ids = (overrides["$g0:$a0"]?.split(" AND ") ?: lic.map { spdx(it.first, it.second) }).distinct()
            val keys = ids.mapNotNull { id ->
                val f = texte.get().file(id.removeSuffix("-only").removeSuffix("-or-later") + ".txt").asFile
                if (f.exists()) { texts[f.nameWithoutExtension] = f.readText().replace("\r\n", "\n").trim(); f.nameWithoutExtension } else null
            }
            val (g, a, v) = coord.split(':')
            mapOf("group" to g, "id" to "$g:$a", "name" to (name ?: a), "version" to v, "license" to (ids.joinToString(" AND ").ifEmpty { "UNKNOWN" }),
                "url" to (url ?: lic.firstOrNull()?.second), "texts" to keys)
        }
        // Ein Eintrag je Gruppe und Lizenz (androidx.compose.ui: ui, ui-graphics, ...)
        val entries = pkgs.groupBy { it["group"] as String to it["license"] as String }.values.map { list ->
            // Version und Seite vom Hauptmodul der Gruppe (kürzester Name: guava statt failureaccess)
            val first = list.minBy { (it["id"] as String).length }
            val versions = list.map { it["version"] }.distinct()
            linkedMapOf<String, Any?>(
                "name" to (if (list.size == 1) first["name"] else first["group"]),
                "version" to (first["version"] as String) + (if (versions.size > 1) " …" else ""),
                "license" to first["license"], "url" to first["url"]
            ).apply {
                if (list.size > 1) put("packages", list.map { "${it["id"]} ${it["version"]}" })
                put("texts", list.flatMap { @Suppress("UNCHECKED_CAST") (it["texts"] as List<String>) }.distinct())
            }.filterValues { it != null }
        }.sortedBy { (it["name"] as String).lowercase() }
        val json = groovy.json.JsonOutput.prettyPrint(groovy.json.JsonOutput.toJson(mapOf("sections" to listOf(mapOf("id" to "app", "entries" to entries)), "texts" to texts))) + "\n"
        val out = ziel.get().asFile
        if (!out.exists() || out.readText() != json) out.writeText(json)
        entries.filter { it["license"] == "UNKNOWN" }.forEach { logger.warn("ossLizenzen: ohne Lizenzangabe: ${it["name"]}") }
    }
}

val ossLizenzen by tasks.registering(OssLizenzen::class) {
    module.set(configurations.named("releaseRuntimeClasspath").flatMap { it.incoming.resolutionResult.rootComponent }.map { root ->
        val seen = linkedSetOf<String>()
        fun walk(c: org.gradle.api.artifacts.result.ResolvedComponentResult) {
            for (d in c.dependencies) if (d is org.gradle.api.artifacts.result.ResolvedDependencyResult) {
                val id = d.selected.id
                if (id is org.gradle.api.artifacts.component.ModuleComponentIdentifier && seen.add("${id.group}:${id.module}:${id.version}")) walk(d.selected)
            }
        }
        walk(root)
        // BOMs (kotlin-bom, compose-bom) legen nur Versionen fest
        seen.filterNot { it.split(':')[1].endsWith("-bom") }
    })
    cache.set(File(gradle.gradleUserHomeDir, "caches/modules-2/files-2.1").path)
    texte.set(rootProject.layout.projectDirectory.dir("../../tools/lizenzen/texte"))
    zusatz.set(rootProject.layout.projectDirectory.file("../../tools/lizenzen/zusatz.json"))
    ziel.set(layout.projectDirectory.file("src/main/assets/third-party.json"))
}
tasks.named("preBuild") { dependsOn(ossLizenzen) }
