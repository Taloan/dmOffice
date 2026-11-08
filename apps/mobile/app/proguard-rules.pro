# OkHttp
-dontwarn okhttp3.**
-dontwarn org.conscrypt.**

# Tresor auf dem Gerät (cryptolib): masterkey.cryptomator wird per Gson in die Felder von MasterkeyFile gelesen
-keep class org.cryptomator.cryptolib.common.MasterkeyFile { *; }
-keep class org.cryptomator.cryptolib.common.MasterkeyFile$* { *; }
-dontwarn org.cryptomator.cryptolib.shaded.**
-dontwarn org.slf4j.**
