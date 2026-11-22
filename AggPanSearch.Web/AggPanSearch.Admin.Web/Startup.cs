using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Newtonsoft.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AggPanSearch.Util;
using AggPanSearch.Util.Model;
using AggPanSearch.Admin.Web.Controllers;
using AggPanSearch.Admin.Web.Areas.UserFrontend.Services;
using Microsoft.Extensions.Logging;

namespace AggPanSearch.Admin.Web
{
    public class Startup
    {
        public IConfiguration Configuration { get; }
        public IWebHostEnvironment WebHostEnvironment { get; set; }

        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            Configuration = configuration;
            WebHostEnvironment = env;
            GlobalContext.LogWhenStart(env);
            GlobalContext.HostingEnvironment = env;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            if (WebHostEnvironment.IsDevelopment())
            {
                services.AddRazorPages().AddRazorRuntimeCompilation();
            }
            services.Configure<CookiePolicyOptions>(options =>
            {
                // This lambda determines whether user consent for non-essential cookies is needed for a given request.
                options.CheckConsentNeeded = context => true;
                options.MinimumSameSitePolicy = SameSiteMode.None;
            });

            services.AddSingleton(HtmlEncoder.Create(UnicodeRanges.All));
            services.AddControllersWithViews(options =>
            {
                options.Filters.Add<GlobalExceptionFilter>();
                options.ModelMetadataDetailsProviders.Add(new ModelBindingMetadataProvider());
            }).AddNewtonsoftJson(options =>
            {
                // 返回数据首字母不小写，CamelCasePropertyNamesContractResolver是小写
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            });

            services.AddScoped<IUserFrontendService, UserFrontendService>();
            // 站点地图生成服务（内部使用进程内静态缓存，作用域注册即可）
            services.AddScoped<SitemapService>();
            // 网盘链接有效性检测服务（单例，内部持有 HttpClient 连接池、TTL 缓存与在途请求合并）
            services.AddSingleton<PanLinkCheckService>();

            // AggPanSearch 网盘搜索服务（注册为单例，复用其内部缓存与 HttpClient）
            services.AddSingleton<AggPanSearch.AggPanSearchService>(sp =>
            {
                // 代理等参数从 appsettings.json 的 AggPanSearch 节读取，改配置即可，无需改代码
                var aggPanSearchSection = Configuration.GetSection("AggPanSearch");
                var proxy = aggPanSearchSection["Proxy"]?.Trim();
                // 日志输出位置：终端/VS 输出窗口（Console） + 同时写入文件，便于任意启动方式下查看
                // 文件写入使用常驻 StreamWriter（FileShare.ReadWrite），避免每次 AppendAllText 开/关文件
                // 被磁盘/杀软拖慢而阻塞搜索线程（日志回调在主线程同步执行）。
                var logFile = Path.Combine(AppContext.BaseDirectory, "pansou.log");
                var logChannel = System.Threading.Channels.Channel.CreateUnbounded<string>();
                var logWriter = new StreamWriter(
                    new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    AutoFlush = false,
                };
                _ = Task.Run(async () =>
                {
                    while (await logChannel.Reader.WaitToReadAsync())
                    {
                        while (logChannel.Reader.TryRead(out var line))
                            logWriter.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {line}");
                        logWriter.Flush();
                    }
                });
                return AggPanSearch.AggPanSearchService.CreateDefault(c =>
                {
                    c.Proxy = string.IsNullOrEmpty(proxy) ? null : proxy;
                    if (int.TryParse(aggPanSearchSection["Concurrency"], out var cc) && cc > 0)
                        c.Concurrency = cc;
                    if (double.TryParse(aggPanSearchSection["AsyncResponseTimeoutSeconds"], out var win) && win > 0)
                        c.AsyncResponseTimeoutSeconds = win;
                    if (int.TryParse(aggPanSearchSection["CacheTtlMinutes"], out var ttl) && ttl > 0)
                        c.CacheTtlMinutes = ttl;
                    c.Log = msg =>
                    {
                        Console.WriteLine($"[AggPanSearch] {msg}");
                        logChannel.Writer.TryWrite(msg);
                    };
                });
            });

            services.AddMemoryCache();
            services.AddSession();
            services.AddHttpContextAccessor();

            services.AddOptions();

            services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(GlobalContext.HostingEnvironment.ContentRootPath + Path.DirectorySeparatorChar + "DataProtection"));

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);  // 注册Encoding

            GlobalContext.SystemConfig = Configuration.GetSection("SystemConfig").Get<SystemConfig>();
            GlobalContext.Services = services;
            GlobalContext.Configuration = Configuration;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (!string.IsNullOrEmpty(GlobalContext.SystemConfig.VirtualDirectory))
            {
                app.UsePathBase(new PathString(GlobalContext.SystemConfig.VirtualDirectory)); // 让 Pathbase 中间件成为第一个处理请求的中间件， 才能正确的模拟虚拟路径
            }
            if (WebHostEnvironment.IsDevelopment())
            {
                GlobalContext.SystemConfig.Debug = true;
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            string resource = Path.Combine(env.ContentRootPath, "Resource");
            FileHelper.CreateDirectory(resource);

            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = GlobalContext.SetCacheControl
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                RequestPath = "/Resource",
                FileProvider = new PhysicalFileProvider(resource),
                OnPrepareResponse = GlobalContext.SetCacheControl
            });
            app.UseSession();
            // 站点根路径默认展示前台首页（UserFrontend/Home/Index），后台首页仍走 /Home/Index
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/")
                {
                    context.Request.Path = "/UserFrontend/Home/Index";
                }
                await next();
            });
            app.UseRouting();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute("areas", "{area:exists}/{controller=Home}/{action=Index}/{id?}");
                endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
            });
            GlobalContext.ServiceProvider = app.ApplicationServices;
        }
    }
}
