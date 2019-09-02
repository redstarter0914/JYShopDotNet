﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Autofac.Extras.DynamicProxy;
using AutoMapper;
using JYShop.Common;
using JYShop.Common.HttpContextUser;
using JYShop.Common.Hubs;
using JYShop.Common.Log;
using JYShop.Common.LogHelper;
using JYShop.Common.MemoryCache;
using JYShopAPI.Aop;
using JYShopAPI.AuthHelper;
using JYShopAPI.Filter;
using JYShopAPI.Middlewares;
using JYShopModel;
using JYShopTasks;
using log4net;
using log4net.Config;
using log4net.Repository;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json.Serialization;
using Swashbuckle.AspNetCore.Swagger;
using static JYShopCommon.Helper.SwaggerHelper.CustomApiVersion;

namespace JYShopAPI
{
    public class Startup
    {
        public static ILoggerRepository loggerRepository { get; set; }

        public IConfiguration Configuration { get; }
        public IHostingEnvironment Env { get; }
        public const string ApiName = "JYShop.Core";


        public Startup(IConfiguration configuration, IHostingEnvironment env)
        {
            Configuration = configuration;
            Env = env;
            loggerRepository = LogManager.CreateRepository(Configuration["Logging:Log4Net:Name"]);

            var contentPath = env.ContentRootPath;
            var log4Config = Path.Combine(contentPath, "log4net.config");
            XmlConfigurator.Configure(loggerRepository, new FileInfo(log4Config));
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            #region 部分服务注入-netcore自带方法
            //缓存注入
            services.AddScoped<ICaching, MemoryCaching>();
            services.AddScoped<IMemoryCache>(factory =>
            {
                var cache = new MemoryCache(new MemoryCacheOptions());
                return cache;
            });


            //redis
            services.AddScoped<IRedisCacheManager, RedisCacheManager>();

            //log
            services.AddScoped<ILoggerHelper, LogHelper>();

            #endregion

            #region 初始化DB
            services.AddScoped<DBSeed>();
            services.AddScoped<MyContext>();

            #endregion

            #region Automapper
            services.AddAutoMapper(typeof(Startup));
            #endregion

            #region CORS 跨域

            services.AddCors(c =>

            {
                //↓↓↓↓↓↓↓注意正式环境不要使用这种全开放的处理↓↓↓↓↓↓↓↓↓↓
                c.AddPolicy("AllRequests", policy =>
                {
                    policy
                        .AllowAnyOrigin()//允许任何源
                        .AllowAnyMethod()//允许任何方式
                        .AllowAnyHeader()//允许任何头
                        .AllowCredentials();//允许cookie
                });
                //↑↑↑↑↑↑↑注意正式环境不要使用这种全开放的处理↑↑↑↑↑↑↑↑↑↑

                //一般采用这种方法
                c.AddPolicy("LimitRequests", policy =>
                 {
                     // 支持多个域名端口，注意端口号后不要带/斜杆：比如localhost:8000/，是错的
                     // 注意，http://127.0.0.1:1818 和 http://localhost:1818 是不一样的，尽量写两个
                     policy
                      .WithOrigins("http://127.0.0.1:1818", "http://127.0.0.1:8080", "http://127.0.0.1:8021")
                      .AllowAnyHeader()
                      .AllowAnyMethod();
                 });
            });

            //跨域第一种办法，注意下边 Configure 中进行配置
            //services.AddCors();
            #endregion

            #region MiniProfiler
            services.AddMiniProfiler(options =>
            {
                options.RouteBasePath = "/profiler";
                //(options.Storage as MemoryCacheStorage).CacheDuration = TimeSpan.FromMinutes(10);
                options.PopupRenderPosition = StackExchange.Profiling.RenderPosition.Left;
                options.PopupShowTimeWithChildren = true;

                // 可以增加权限
                options.ResultsAuthorize = request => request.HttpContext.User.IsInRole("Admin");
                options.UserIdProvider = request => request.HttpContext.User.Identity.Name;
            });
            #endregion

            #region Swagger UI Service
            var basePath = Microsoft.DotNet.PlatformAbstractions.ApplicationEnvironment.ApplicationBasePath;
            services.AddSwaggerGen(c =>
            {
                typeof(ApiVersions).GetEnumNames().ToList().ForEach(version =>
                {
                    c.SwaggerDoc(version, new Info
                    {
                        Version = version,
                        Title = $"{ApiName} 接口文档",
                        Description = $"{ApiName} HTTP Api " + version,
                        TermsOfService = "None",
                        Contact = new Contact { Name = "JYShop.Core", Email = "aa@qq.com", Url = "http://www.baidu.com" }

                    });
                    // 按相对路径排序，作者：Alby
                    c.OrderActionsBy(o => o.RelativePath);
                });

                var xmlpath = Path.Combine(basePath, "JYShop.Core.xml");//这个就是刚刚配置的xml文件名
                c.IncludeXmlComments(xmlpath, true);//默认的第二个参数是false，这个是controller的注释，记得修改

                var xmlmodelpath = Path.Combine(basePath, "JYShop.Core.Model.xml");//这个就是Model层的xml文件名
                c.IncludeXmlComments(xmlmodelpath);

                #region Token绑定到ConfigureServices

                //添加header验证信息
                //c.OperationFilter<SwaggerHeader>();

                var IssureName = (Configuration.GetSection("Audience"))["Issuer"];

                var security = new Dictionary<string, IEnumerable<string>> { { IssureName, new string[] { } }, };
                //var security = new Dictionary<string, IEnumerator<string>> { { IssureName, new string[] { } }, };

                c.AddSecurityRequirement(security);

                //方案名称“JYShop.Core”可自定义，上下一致即可
                c.AddSecurityDefinition(IssureName, new ApiKeyScheme
                {
                    Description = "JWT授权(数据将在请求头中进行传输) 直接在下框中输入Bearer {token}（注意两者之间是一个空格）\"",
                    Name = "Authorization",//jwt默认的参数名称
                    In = "header",//jwt默认存放Authorization信息的位置(请求头中)
                    Type = "apiKey"
                });


                #endregion
            });

            #endregion

            #region MVC + GlobalExceptions

            //注入全局异常捕获

            services.AddMvc(o =>
            {
                // 全局异常过滤
                o.Filters.Add(typeof(GlobalExceptionsFilter));
                // 全局路由权限公约
                o.Conventions.Insert(0, new GlobalRouteAuthorizeConvention());
                // 全局路由前缀，统一修改路由
                o.Conventions.Insert(0, new GlobalRoutePrefixFilter(new RouteAttribute(RoutePrefix.Name)));

            }).SetCompatibilityVersion(CompatibilityVersion.Version_2_2)
            .AddJsonOptions(options =>
            {
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            });

            #endregion

            #region TimedJob
            services.AddHostedService<Job1TimedService>();

            #endregion

            #region Httpcontext
            // Httpcontext 注入
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddScoped<IUser, AspNetUser>();
            #endregion

            #region SignalR 通讯
            services.AddSignalR();
            #endregion

            #region Authorize 权限认证三步走

            //使用说明：

            //1、如果你只是简单的基于角色授权的，仅仅在 api 上配置，第一步：【1/2 简单角色授权】，第二步：配置【统一认证服务】，第三步：开启中间件

            //2、如果你是用的复杂的基于策略授权，配置权限在数据库，第一步：【3复杂策略授权】，第二步：配置【统一认证服务】，第三步：开启中间件app.UseAuthentication();

            //3、综上所述，设置权限，必须要三步走，授权 + 配置认证服务 + 开启授权中间件，只不过自定义的中间件不能验证过期时间，所以我都是用官方的。

            #region 【第一步：授权】

            #region 1、基于角色的API授权 

            // 1【授权】、这个很简单，其他什么都不用做， 只需要在API层的controller上边，增加特性即可，注意，只能是角色的:
            // [Authorize(Roles = "Admin,System")]


            #endregion

            #region 2、基于策略的授权（简单版）

            // 1【授权】、这个和上边的异曲同工，好处就是不用在controller中，写多个 roles 。
            // 然后这么写 [Authorize(Policy = "Admin")]
            services.AddAuthorization(options =>
            {
                options.AddPolicy("Client", policy => policy.RequireRole("Client").Build());
                options.AddPolicy("Admin", policy => policy.RequireRole("Admin").Build());
                options.AddPolicy("SystemAdmin", policy => policy.RequireRole("Admin", "System").Build());
            });

            #endregion


            #region 【3、复杂策略授权】

            #region 参数
            //读取配置文件
            var audienceConfig = Configuration.GetSection("Audience");
            var symmetricKeyAsBase64 = audienceConfig["Secret"];
            var KeyByteArray = Encoding.ASCII.GetBytes(symmetricKeyAsBase64);
            var signingKey = new SymmetricSecurityKey(KeyByteArray);

            var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

            // 如果要数据库动态绑定，这里先留个空，后边处理器里动态赋值
            var permission = new List<PermissionItem>();

            // 角色与接口的权限要求参数
            var permissionRequirement = new PermissionRequirement(
                "/api/denied",// 拒绝授权的跳转地址（目前无用）
                permission,
                ClaimTypes.Role,//基于角色的授权
                audienceConfig["Issuer"],//发行人
                audienceConfig["Audience"],//听众
                signingCredentials,//签名凭据
                expiration: TimeSpan.FromSeconds(60 * 60)//接口的过期时间
                );

            #endregion

            //【授权】
            services.AddAuthorization(options =>
            {
                options.AddPolicy(Permissions.Name, policy => policy.Requirements.Add(permissionRequirement));
            });

            #endregion

            #endregion

            #region 【第二步：配置认证服务】
            // 令牌验证参数
            var tokenValidationParameter = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateIssuer = true,
                ValidIssuer = audienceConfig["Issuer"],
                ValidateAudience = true,
                ValidAudience = audienceConfig["Audience"],
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RequireExpirationTime = true
            };

            //2.1【认证】、core自带官方JWT认证
            // 开启Bearer认证
            services.AddAuthentication("Bearer")
            // 添加JwtBearer服务
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = tokenValidationParameter;
                o.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // 如果过期，则把<是否过期>添加到，返回头信息中
                        if (context.Exception.GetType() == typeof(SecurityTokenExpiredException))
                        {
                            context.Response.Headers.Add("Token-Expired", "true");
                        }
                        return Task.CompletedTask;
                    }
                };
            });

            //2.2【认证】、IdentityServer4 认证 (暂时忽略)
            //services.AddAuthentication("Bearer")
            //  .AddIdentityServerAuthentication(options =>
            //  {
            //      options.Authority = "http://localhost:5002";
            //      options.RequireHttpsMetadata = false;
            //      options.ApiName = "blog.core.api";
            //  });

            // 注入权限处理器
            services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
            services.AddSingleton(permissionRequirement);

            #endregion

            #endregion

            services.AddSingleton(new Appsettings(Env));
            services.AddSingleton(new LogLock(Env));

            #region AutoFac DI

            //实例化 AutoFac  容器   
            var builder = new ContainerBuilder();
            //注册要通过反射创建的组件
            //builder.RegisterType<AdvertisementServices>().As<IAdvertisementServices>();
            builder.RegisterType<ShopCacheAop>();
            builder.RegisterType<ShopRedisCacheAOP>();
            builder.RegisterType<ShopLogAop>();


            // ※※★※※ 如果你是第一次下载项目，请先F6编译，然后再F5执行，※※★※※

            #region 带有接口层的服务注入

            try
            {
                #region Service.dll 注入，有对应接口
                //获取项目绝对路径，请注意，这个是实现类的dll文件，不是接口 IService.dll ，注入容器当然是Activatore

                var servicesDLLFile = Path.Combine(basePath, "JYShop.Services.dll");
                //直接采用加载文件的方法  ※※★※※ 如果你是第一次下载项目，请先F6编译，然后再F5执行，※※★※※
                var assymblysServices = Assembly.LoadFrom(servicesDLLFile);

                //指定已扫描程序集中的类型注册为提供所有其实现的接口。
                // builder.RegisterAssemblyTypes(assymblysServices).AsImplementedInterfaces();
                var cacheType = new List<Type>();
                if (Appsettings.app(new string[] { "AppSettings", "RedisCachingAOP", "Enabled" }).ObjToBool())
                {
                    cacheType.Add(typeof(ShopRedisCacheAOP));
                }
                if (Appsettings.app(new string[] { "AppSettings", "MemoryCachingAOP", "Enabled" }).ObjToBool())
                {
                    cacheType.Add(typeof(ShopCacheAop));
                }
                if (Appsettings.app(new string[] { "AppSettings", "LogAOP", "Enabled" }).ObjToBool())
                {
                    cacheType.Add(typeof(ShopLogAop));
                }

                builder.RegisterAssemblyTypes(assymblysServices)
                    .AsImplementedInterfaces()
                    .InstancePerLifetimeScope()
                    //引用Autofac.Extras.DynamicProxy;
                    .EnableInterfaceInterceptors()
                    // 如果你想注入两个，就这么写  InterceptedBy(typeof(BlogCacheAOP), typeof(BlogLogAOP));
                    // 如果想使用Redis缓存，请必须开启 redis 服务，端口号我的是6319，如果不一样还是无效，否则请使用memory缓存 BlogCacheAOP
                    .InterceptedBy(cacheType.ToArray());//允许将拦截器服务的列表分配给注册。 

                #endregion


                #region Repository.dll 注入，有对应接口
                var repositoryDllFile = Path.Combine(basePath, "JYShop.Repository.dll");
                var assemblysRepository = Assembly.LoadFrom(repositoryDllFile);
                builder.RegisterAssemblyTypes(assemblysRepository).AsImplementedInterfaces();

                #endregion

            }
            catch (Exception ex)
            {
                throw new Exception("※※★※※ 如果你是第一次下载项目，请先对整个解决方案dotnet build（F6编译），然后再对api层 dotnet run（F5执行），\n因为解耦了，如果你是发布的模式，请检查bin文件夹是否存在Repository.dll和service.dll ※※★※※" + ex.Message + "\n" + ex.InnerException);
            }
            #endregion

            #region 没有接口层的服务层注入

            ////因为没有接口层，所以不能实现解耦，只能用 Load 方法。
            ////注意如果使用没有接口的服务，并想对其使用 AOP 拦截，就必须设置为虚方法
            ////var assemblysServicesNoInterfaces = Assembly.Load("Blog.Core.Services");
            ////builder.RegisterAssemblyTypes(assemblysServicesNoInterfaces);

            #endregion


            #region 没有接口的单独类 class 注入
            ////只能注入该类中的虚方法
            //builder.RegisterAssemblyTypes(Assembly.GetAssembly(typeof(Love)))
            //    .EnableClassInterceptors()
            //    .InterceptedBy(typeof(BlogLogAOP));

            #endregion

            #endregion

            //将services填充到Autofac容器生成器中
            builder.Populate(services);

            var ApplicationContainer = builder.Build();

            return new AutofacServiceProvider(ApplicationContainer);////第三方IOC接管 core内置DI容器

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {

            #region ReuestResponseLog
            if (Appsettings.app("AppSettings", "Middleware_RequestResponse", "Enabled").ObjToBool())
            {
                app.UseReuestResponseLog();//记录请求与返回数据 
            }
            #endregion

            #region Environment

            if (env.IsDevelopment())
            {
                // 在开发环境中，使用异常页面，这样可以暴露错误堆栈信息，所以不要放在生产环境。
                app.UseDeveloperExceptionPage();

                //app.Use(async (context, next) =>
                //{
                //    //这里会多次调用，这里测试一下就行，不要打开注释
                //    //var blogs =await _blogArticleServices.GetBlogs();
                //    var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                //    Console.WriteLine(processName);
                //    await next();
                //});
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // 在非开发环境中，使用HTTP严格安全传输(or HSTS) 对于保护web安全是非常重要的。
                // 强制实施 HTTPS 在 ASP.NET Core，配合 app.UseHttpsRedirection
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                //app.UseHsts();
            }

            #endregion

            #region Swagger
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                //根据版本名称倒序 遍历展示
                typeof(ApiVersions).GetEnumNames().OrderByDescending(e => e).ToList().ForEach(version =>
                {
                    c.SwaggerEndpoint($"/swagger/{version}/swagger.json", $"{ApiName}{version}");
                    // 将swagger首页，设置成我们自定义的页面，记得这个字符串的写法：解决方案名.index.html
                    c.IndexStream = () => GetType().GetTypeInfo().Assembly.GetManifestResourceStream("JYShop.index.html");//这里是配合MiniProfiler进行性能监控的，《文章：完美基于AOP的接口性能分析》，如果你不需要，可以暂时先注释掉，不影响大局。
                    c.RoutePrefix = ""; //路径配置，设置为空，表示直接在根域名（localhost:8001）访问该文件,注意localhost:8001/swagger是访问不到的，去launchSettings.json把launchUrl去掉
                });
            });

            #endregion

            #region MiniProfiler
            app.UseMiniProfiler();
            #endregion

            #region 第三步：开启认证中间件
            //此授权认证方法已经放弃，请使用下边的官方验证方法。但是如果你还想传User的全局变量，还是可以继续使用中间件，第二种写法
            //app.UseMiddleware<JwtTokenAuth>(); 
            app.UseJwtTokenAuth();

            //如果你想使用官方认证，必须在上边ConfigureService 中，配置JWT的认证服务 (.AddAuthentication 和 .AddJwtBearer 二者缺一不可)
            app.UseAuthentication();

            #endregion

            #region CORS
            //跨域第二种方法，使用策略，详细策略信息在ConfigureService中
            app.UseCors("LimitRequests");//将 CORS 中间件添加到 web 应用程序管线中, 以允许跨域请求。

            #region 跨域第一种版本
            //跨域第一种版本，请要ConfigureService中配置服务 services.AddCors();
            //    app.UseCors(options => options.WithOrigins("http://localhost:8021").AllowAnyHeader()
            //.AllowAnyMethod());  
            #endregion

            #endregion

            // 跳转https
            //app.UseHttpsRedirection(); 
            // 使用静态文件
            app.UseStaticFiles();
            // 使用cookie
            app.UseCookiePolicy();
            // 返回错误码
            app.UseStatusCodePages();//把错误码返回前台，比如是404

            app.UseMvc();

            #region SignalR
            app.UseSignalR(routes=> {
                //这里要说下，为啥地址要写 /api/xxx 
                //因为我前后端分离了，而且使用的是代理模式，所以如果你不用/api/xxx的这个规则的话，会出现跨域问题，毕竟这个不是我的controller的路由，而且自己定义的路由
                routes.MapHub<ChatHub>("/api2/chathub");
            });

            #endregion
        }
    } 
}
