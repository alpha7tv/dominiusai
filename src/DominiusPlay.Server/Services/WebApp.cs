using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace DominiusPlay.Server.Services;

/// <summary>
/// Publica a versão para navegador (as mesmas telas do aplicativo, em WebAssembly) no endereço raiz do servidor.
/// Os arquivos ficam em &lt;pasta do servidor&gt;/wwwroot (o instalador Linux copia para lá). Sem a pasta, nada muda.
/// </summary>
public static class WebApp
{
    public static void UseCrmWebApp(this WebApplication app)
    {
        var root = app.Configuration["WebRoot"] is { Length: > 0 } r ? r : Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(root, "index.html"))) return;

        var files = new PhysicalFileProvider(root);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".dat"] = "application/octet-stream";
        types.Mappings[".blat"] = "application/octet-stream";
        types.Mappings[".dll"] = "application/octet-stream";
        types.Mappings[".webcil"] = "application/octet-stream";
        types.Mappings[".pdb"] = "application/octet-stream";
        types.Mappings[".map"] = "application/json";

        // Entrega a versão pré-comprimida (.br / .gz) gerada na publicação: ~6 MB em vez de ~20 MB no primeiro acesso
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            if (path == "/") path = "/index.html";
            if (HttpMethods.IsGet(ctx.Request.Method) && !path.StartsWith("/api/") && !path.StartsWith("/hubs/") && types.TryGetContentType(path, out var type))
            {
                var accept = ctx.Request.Headers.AcceptEncoding.ToString();
                foreach (var (enc, ext) in new[] { ("br", ".br"), ("gzip", ".gz") })
                {
                    if (!accept.Contains(enc, StringComparison.OrdinalIgnoreCase)) continue;
                    var fi = files.GetFileInfo(path + ext);
                    if (!fi.Exists) continue;
                    ctx.Response.ContentType = type;
                    ctx.Response.Headers.ContentEncoding = enc;
                    ctx.Response.Headers.Vary = "Accept-Encoding";
                    ctx.Response.Headers.CacheControl = CacheControlFor(path);
                    ctx.Response.ContentLength = fi.Length;
                    await ctx.Response.SendFileAsync(fi);
                    return;
                }
            }
            await next();
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files, ContentTypeProvider = types,
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = CacheControlFor(c.Context.Request.Path.Value ?? ""),
        });
    }

    /// <summary>Arquivos com impressão digital no nome (_framework) podem ficar em cache; a página e o main.js sempre revalidam.</summary>
    private static string CacheControlFor(string path) =>
        Fingerprinted.IsMatch(path) ? "public, max-age=31536000, immutable" : "no-cache";

    private static readonly System.Text.RegularExpressions.Regex Fingerprinted = new(@"^/_framework/.+\.[a-z0-9]{10}\.[a-z]+$");
}
