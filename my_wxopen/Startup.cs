using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Senparc.CO2NET;
using Senparc.CO2NET.Cache.Redis;
using Senparc.CO2NET.RegisterServices;
using Senparc.Weixin.Entities;
using Senparc.Weixin.RegisterServices;

using Senparc.CO2NET.Cache;
using Senparc.Weixin;
using Senparc.Weixin.Cache.Redis;
using Senparc.Weixin.MP;
using Senparc.Weixin.WxOpen;

namespace my_wxopen
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddMemoryCache();//使用本地缓存必须添加
            services.AddSession();//使用Session

            services.AddSignalR();//使用 SignalR

            /*
             * CO2NET 是从 Senparc.Weixin 分离的底层公共基础模块，经过了长达 6 年的迭代优化，稳定可靠。
             * 关于 CO2NET 在所有项目中的通用设置可参考 CO2NET 的 Sample：
             * https://github.com/Senparc/Senparc.CO2NET/blob/master/Sample/Senparc.CO2NET.Sample.netcore/Startup.cs
             */

            services.AddSenparcGlobalServices(Configuration)//Senparc.CO2NET 全局注册
                    .AddSenparcWeixinServices(Configuration);//Senparc.Weixin 注册
                  //  .AddSenparcWebSocket<CustomNetCoreWebSocketMessageHandler>();//Senparc.WebSocket 注册（按需）
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, IOptions<SenparcSetting> senparcSetting, IOptions<SenparcWeixinSetting> senparcWeixinSetting)
        {
            //引入EnableRequestRewind中间件
            app.UseEnableRequestRewind();
            app.UseSession();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
               // app.UseBrowserLink();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
            }

            app.UseStaticFiles();

            app.UseMvc(routes =>
            {
                routes.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");
            });
            // 启动 CO2NET 全局注册，必须！
            IRegisterService register = RegisterService.Start(env, senparcSetting.Value)
                                                        //关于 UseSenparcGlobal() 的更多用法见 CO2NET Demo：https://github.com/Senparc/Senparc.CO2NET/blob/master/Sample/Senparc.CO2NET.Sample.netcore/Startup.cs
                                                        .UseSenparcGlobal();
            #region CO2NET 全局配置
            //当同一个分布式缓存同时服务于多个网站（应用程序池）时，可以使用命名空间将其隔离（非必须）
            register.ChangeDefaultCacheNamespace("DefaultCO2NETCache");
            //配置全局使用Redis缓存（按需，独立）
            var redisConfigurationStr = senparcSetting.Value.Cache_Redis_Configuration;
            var useRedis = !string.IsNullOrEmpty(redisConfigurationStr) && redisConfigurationStr != "#{Cache_Redis_Configuration}#"/*默认值，不启用*/;
            if (useRedis)//这里为了方便不同环境的开发者进行配置，做成了判断的方式，实际开发环境一般是确定的，这里的if条件可以忽略
            {
                /* 说明：
                 * 1、Redis 的连接字符串信息会从 Config.SenparcSetting.Cache_Redis_Configuration 自动获取并注册，如不需要修改，下方方法可以忽略
                /* 2、如需手动修改，可以通过下方 SetConfigurationOption 方法手动设置 Redis 链接信息（仅修改配置，不立即启用）
                 */
                Senparc.CO2NET.Cache.Redis.Register.SetConfigurationOption(redisConfigurationStr);

                //以下会立即将全局缓存设置为 Redis
                Senparc.CO2NET.Cache.Redis.Register.UseKeyValueRedisNow();//键值对缓存策略（推荐）
                //Senparc.CO2NET.Cache.Redis.Register.UseHashRedisNow();//HashSet储存格式的缓存策略

                //也可以通过以下方式自定义当前需要启用的缓存策略
                //CacheStrategyFactory.RegisterObjectCacheStrategy(() => RedisObjectCacheStrategy.Instance);//键值对
                //CacheStrategyFactory.RegisterObjectCacheStrategy(() => RedisHashSetObjectCacheStrategy.Instance);//HashSet
            }
            //如果这里不进行Redis缓存启用，则目前还是默认使用内存缓存 
            #region 注册日志（按需，建议）

            register.RegisterTraceLog(ConfigTraceLog);//配置TraceLog

            #endregion

            Senparc.CO2NET.APM.Config.DataExpire = TimeSpan.FromMinutes(60);//测试APM缓存过期时间（默认情况下可以不用设置）

            #endregion

            #region 微信相关配置
            /* 微信配置开始
             * 
             * 建议按照以下顺序进行注册，尤其须将缓存放在第一位！
             */

            //注册开始

            #region 微信缓存（按需，必须在 register.UseSenparcWeixin() 之前）

            //微信的 Redis 缓存，如果不使用则注释掉（开启前必须保证配置有效，否则会抛错）         -- DPBMARK Redis
            if (useRedis)
            {
                app.UseSenparcWeixinCacheRedis();
            }                                                                                     // DPBMARK_END
            #endregion
            //开始注册微信信息，必须！
            register.UseSenparcWeixin(senparcWeixinSetting.Value, senparcSetting.Value)
                //注意：上一行没有 ; 下面可接着写 .RegisterXX()

            #region 注册公众号或小程序（按需）

                //注册公众号（可注册多个）                                                -- DPBMARK MP
                .RegisterMpAccount(senparcWeixinSetting.Value, "【盛派网络小助手】公众号")// DPBMARK_END


                //注册多个公众号或小程序（可注册多个）                                        -- DPBMARK MiniProgram
                .RegisterWxOpenAccount(senparcWeixinSetting.Value, "【盛派网络小助手】小程序");// DPBMARK_END

                //除此以外，仍然可以在程序任意地方注册公众号或小程序：
                //AccessTokenContainer.Register(appId, appSecret, name);//命名空间：Senparc.Weixin.MP.Containers
            #endregion
            #endregion
        }

        /// <summary>
        /// 配置微信跟踪日志
        /// </summary>
        private void ConfigTraceLog()
        {
            //这里设为Debug状态时，/App_Data/WeixinTraceLog/目录下会生成日志文件记录所有的API请求日志，正式发布版本建议关闭

            //如果全局的IsDebug（Senparc.CO2NET.Config.IsDebug）为false，此处可以单独设置true，否则自动为true
            Senparc.CO2NET.Trace.SenparcTrace.SendCustomLog("系统日志", "系统启动");//只在Senparc.Weixin.Config.IsDebug = true的情况下生效

            //全局自定义日志记录回调
            Senparc.CO2NET.Trace.SenparcTrace.OnLogFunc = () =>
            {
                //加入每次触发Log后需要执行的代码
            };

            //当发生基于WeixinException的异常时触发
            WeixinTrace.OnWeixinExceptionFunc = ex =>
            {
                //加入每次触发WeixinExceptionLog后需要执行的代码

                //发送模板消息给管理员                             -- DPBMARK Redis
                //var eventService = new Senparc.Weixin.MP.Sample.CommonService.EventService();
                //eventService.ConfigOnWeixinExceptionFunc(ex);      // DPBMARK_END
            };
        }
    }
}
