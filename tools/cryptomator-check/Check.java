// Gegenprobe für das Cryptomator-Format von dmOffice (docs/tresor-cryptomator-konzept.md, Abschnitt 7) mit den Bibliotheken
// der installierten Cryptomator-App (cryptofs, cryptolib). Aufruf über check.ps1.
//
//   create <tresor> <passwort> <quelle>   Tresor mit Cryptomator anlegen und den Inhalt von <quelle> hineinschreiben
//   dump   <tresor> <passwort>            Inhalt auflisten: Art, Pfad, Größe, SHA-256 (sortiert, eine Zeile je Eintrag)
//
// Ohne Verweise zur Übersetzungszeit (nur Reflexion): Die Bibliotheken der App sind für eine neuere Java-Fassung übersetzt als
// manches installierte JDK; so genügt zum Übersetzen Java 17, laufen muss es mit der Laufzeit der App.

import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.net.URI;
import java.nio.file.*;
import java.nio.file.attribute.BasicFileAttributes;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.*;
import java.util.stream.Stream;

public class Check {
    static Method find(Class<?> c, String name, int params) {
        // Überladungen mit Path bevorzugen (load/persist gibt es auch mit Streams)
        for (var m : c.getMethods())
            if (m.getName().equals(name) && m.getParameterCount() == params && Arrays.asList(m.getParameterTypes()).contains(Path.class)) return m;
        for (var m : c.getMethods()) if (m.getName().equals(name) && m.getParameterCount() == params) return m;
        throw new IllegalStateException(c.getName() + "." + name + "/" + params + " fehlt");
    }

    public static void main(String[] args) throws Exception {
        var vault = Paths.get(args[1]);
        var password = args[2];
        var accessClass = Class.forName("org.cryptomator.cryptolib.common.MasterkeyFileAccess");
        var access = accessClass.getConstructor(byte[].class, SecureRandom.class).newInstance(new byte[0], SecureRandom.getInstanceStrong());
        var masterkeyFile = vault.resolve("masterkey.cryptomator");
        var load = find(accessClass, "load", 2);
        var loaderClass = Class.forName("org.cryptomator.cryptolib.api.MasterkeyLoader");
        var loader = Proxy.newProxyInstance(loaderClass.getClassLoader(), new Class<?>[]{loaderClass}, (proxy, m, a) -> {
            if (m.getName().equals("loadKey")) return load.invoke(access, masterkeyFile, password);
            if (m.getName().equals("toString")) return "loader";
            if (m.getName().equals("hashCode")) return System.identityHashCode(proxy);
            if (m.getName().equals("equals")) return proxy == a[0];
            throw new UnsupportedOperationException(m.getName());
        });
        var propsClass = Class.forName("org.cryptomator.cryptofs.CryptoFileSystemProperties");
        var builder = find(propsClass, "cryptoFileSystemProperties", 0).invoke(null);
        builder = find(builder.getClass(), "withKeyLoader", 1).invoke(builder, loader);
        var props = find(builder.getClass(), "build", 0).invoke(builder);
        var provider = Class.forName("org.cryptomator.cryptofs.CryptoFileSystemProvider");
        var newFs = find(provider, "newFileSystem", 2);
        switch (args[0]) {
            case "create" -> {
                Files.createDirectories(vault);
                var mkClass = Class.forName("org.cryptomator.cryptolib.api.Masterkey");
                var mk = find(mkClass, "generate", 1).invoke(null, SecureRandom.getInstanceStrong());
                find(accessClass, "persist", 3).invoke(access, mk, masterkeyFile, password);
                find(provider, "initialize", 3).invoke(null, vault, props, URI.create("masterkeyfile:masterkey.cryptomator"));
                var source = Paths.get(args[3]);
                try (var fs = (FileSystem) newFs.invoke(null, vault, props); Stream<Path> walk = Files.walk(source)) {
                    for (var p : walk.sorted().toList()) {
                        var rel = source.relativize(p).toString().replace('\\', '/');
                        if (rel.isEmpty()) continue;
                        var target = fs.getPath("/" + rel);
                        if (Files.isDirectory(p)) Files.createDirectories(target);
                        else Files.copy(p, target, StandardCopyOption.REPLACE_EXISTING);
                    }
                }
                System.out.println("OK");
            }
            case "dump" -> {
                var lines = new ArrayList<String>();
                try (var fs = (FileSystem) newFs.invoke(null, vault, props)) {
                    var root = fs.getPath("/");
                    Files.walkFileTree(root, new SimpleFileVisitor<>() {
                        @Override public FileVisitResult preVisitDirectory(Path d, BasicFileAttributes a) {
                            if (!d.equals(root)) lines.add("D\t" + d + "\t0\t-");
                            return FileVisitResult.CONTINUE;
                        }
                        @Override public FileVisitResult visitFile(Path f, BasicFileAttributes a) throws java.io.IOException {
                            try {
                                var md = MessageDigest.getInstance("SHA-256");
                                try (var in = Files.newInputStream(f)) {
                                    var buf = new byte[65536];
                                    for (int n; (n = in.read(buf)) > 0; ) md.update(buf, 0, n);
                                }
                                lines.add("F\t" + f + "\t" + a.size() + "\t" + HexFormat.of().formatHex(md.digest()));
                            } catch (java.security.NoSuchAlgorithmException e) { throw new RuntimeException(e); }
                            return FileVisitResult.CONTINUE;
                        }
                    });
                }
                Collections.sort(lines);
                var out = new java.io.PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, java.nio.charset.StandardCharsets.UTF_8);
                lines.forEach(out::println);
            }
            default -> throw new IllegalArgumentException("create oder dump");
        }
    }
}
