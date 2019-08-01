using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using my_wxopen.MessageHandler;
using Senparc.CO2NET.HttpUtility;
using Senparc.CO2NET.Utilities;
using Senparc.Weixin;
using Senparc.Weixin.MP;
using Senparc.Weixin.MP.MvcExtension;
using Senparc.Weixin.WxOpen.AdvancedAPIs.Sns;
using Senparc.Weixin.WxOpen.Containers;
using Senparc.Weixin.WxOpen.Entities;
using Senparc.Weixin.WxOpen.Entities.Request;
using Senparc.Weixin.WxOpen.Helpers;

// For more information on enabling MVC for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace my_wxopen.Controllers
{
    /// <summary>
    /// 微信小程序Controller
    /// </summary>
    public partial class WxOpenController : Controller
    {
        public static readonly string Token = Config.SenparcWeixinSetting.WxOpenToken;//与微信小程序后台的Token设置保持一致，区分大小写。
        public static readonly string EncodingAESKey = Config.SenparcWeixinSetting.WxOpenEncodingAESKey;//与微信小程序后台的EncodingAESKey设置保持一致，区分大小写。
        public static readonly string WxOpenAppId = Config.SenparcWeixinSetting.WxOpenAppId;//与微信小程序后台的AppId设置保持一致，区分大小写。
        public static readonly string WxOpenAppSecret = Config.SenparcWeixinSetting.WxOpenAppSecret;//与微信小程序账号后台的AppId设置保持一致，区分大小写。


        readonly Func<string> _getRandomFileName = () => SystemTime.Now.ToString("yyyyMMdd-HHmmss") + Guid.NewGuid().ToString("n").Substring(0, 6);


        /// <summary>
        /// GET请求用于处理微信小程序后台的URL验证
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [ActionName("Index")]
        public ActionResult Get(PostModel postModel, string echostr)
        {
            if (CheckSignature.Check(postModel.Signature, postModel.Timestamp, postModel.Nonce, Token))
            {
                return Content(echostr); //返回随机字符串则表示验证通过
            }
            else
            {
                return Content("failed:" + postModel.Signature + "," + Senparc.Weixin.MP.CheckSignature.GetSignature(postModel.Timestamp, postModel.Nonce, Token) + "。" +
                    "如果你在浏览器中看到这句话，说明此地址可以被作为微信小程序后台的Url，请注意保持Token一致。");
            }
        }

        /// <summary>
        /// 用户发送消息后，微信平台自动Post一个请求到这里，并等待响应XML。
        /// </summary>
        [HttpPost]
        [ActionName("Index")]
        public ActionResult Post(PostModel postModel)
        {
            if (!CheckSignature.Check(postModel.Signature, postModel.Timestamp, postModel.Nonce, Token))
            {
                return Content("参数错误！");
            }

            postModel.Token = Token;//根据自己后台的设置保持一致
            postModel.EncodingAESKey = EncodingAESKey;//根据自己后台的设置保持一致
            postModel.AppId = WxOpenAppId;//根据自己后台的设置保持一致（必须提供）

            //v4.2.2之后的版本，可以设置每个人上下文消息储存的最大数量，防止内存占用过多，如果该参数小于等于0，则不限制
            var maxRecordCount = 10;

            var logPath = ServerUtility.ContentRootMapPath(string.Format("~/App_Data/WxOpen/{0}/", SystemTime.Now.ToString("yyyy-MM-dd")));
            if (!Directory.Exists(logPath))
            {
                Directory.CreateDirectory(logPath);
            }

            //自定义MessageHandler，对微信请求的详细判断操作都在这里面。
            var messageHandler = new WxOpenMessageHandler(Request.GetRequestMemoryStream(), postModel, maxRecordCount);


            try
            {
                /* 如果需要添加消息去重功能，只需打开OmitRepeatedMessage功能，SDK会自动处理。
                 * 收到重复消息通常是因为微信服务器没有及时收到响应，会持续发送2-5条不等的相同内容的RequestMessage*/
                messageHandler.OmitRepeatedMessage = true;

                //测试时可开启此记录，帮助跟踪数据，使用前请确保App_Data文件夹存在，且有读写权限。
                messageHandler.SaveRequestMessageLog();//记录 Request 日志（可选）

                messageHandler.Execute();//执行微信处理过程（关键）

                messageHandler.SaveResponseMessageLog();//记录 Response 日志（可选）

                //return Content(messageHandler.ResponseDocument.ToString());//v0.7-
                //return new FixWeixinBugWeixinResult(messageHandler);//为了解决官方微信5.0软件换行bug暂时添加的方法，平时用下面一个方法即可
                return new WeixinResult(messageHandler);                                                   //return new WeixinResult(messageHandler);//v0.8+
            }
            catch (Exception ex)
            {
                using (TextWriter tw = new StreamWriter(ServerUtility.ContentRootMapPath("~/App_Data/Error_WxOpen_" + _getRandomFileName() + ".txt")))
                {
                    tw.WriteLine("ExecptionMessage:" + ex.Message);
                    tw.WriteLine(ex.Source);
                    tw.WriteLine(ex.StackTrace);
                    //tw.WriteLine("InnerExecptionMessage:" + ex.InnerException.Message);

                    if (messageHandler.ResponseDocument != null)
                    {
                        tw.WriteLine(messageHandler.ResponseDocument.ToString());
                    }

                    if (ex.InnerException != null)
                    {
                        tw.WriteLine("========= InnerException =========");
                        tw.WriteLine(ex.InnerException.Message);
                        tw.WriteLine(ex.InnerException.Source);
                        tw.WriteLine(ex.InnerException.StackTrace);
                    }

                    tw.Flush();
                    tw.Close();
                }
                return Content("");
            }
        }

        [HttpPost]
        public ActionResult RequestData(string nickName)
        {
            var data = new
            {
                msg = string.Format("服务器时间：{0}，昵称：{1}", SystemTime.Now.LocalDateTime, nickName)
            };
            return Json(data);
        }

        /// <summary>
        /// wx.login登陆成功之后发送的请求
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        [HttpPost]
        public ActionResult OnLogin(string code)
        {
            try
            {
                var jsonResult = SnsApi.JsCode2Json(WxOpenAppId, WxOpenAppSecret, code);
                if (jsonResult.errcode == ReturnCode.请求成功)
                {
                    //Session["WxOpenUser"] = jsonResult;//使用Session保存登陆信息（不推荐）
                    //使用SessionContainer管理登录信息（推荐）
                    var unionId = "";
                    var sessionBag = SessionContainer.UpdateSession(null, jsonResult.openid, jsonResult.session_key, unionId);

                    //注意：生产环境下SessionKey属于敏感信息，不能进行传输！
                    return Json(new { success = true, msg = "OK", sessionId = sessionBag.Key, sessionKey = sessionBag.SessionKey });
                }
                else
                {
                    return Json(new { success = false, msg = jsonResult.errmsg });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, msg = ex.Message });
            }

        }

        [HttpPost]
        public ActionResult CheckWxOpenSignature(string sessionId, string rawData, string signature)
        {
            try
            {
                var checkSuccess = Senparc.Weixin.WxOpen.Helpers.EncryptHelper.CheckSignature(sessionId, rawData, signature);
                return Json(new { success = checkSuccess, msg = checkSuccess ? "签名校验成功" : "签名校验失败" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, msg = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DecodeEncryptedData(string type, string sessionId, string encryptedData, string iv)
        {
            DecodeEntityBase decodedEntity = null;
            switch (type.ToUpper())
            {
                case "USERINFO"://wx.getUserInfo()
                    decodedEntity = Senparc.Weixin.WxOpen.Helpers.EncryptHelper.DecodeUserInfoBySessionId(
                        sessionId,
                        encryptedData, iv);
                    break;
                default:
                    break;
            }

            //检验水印
            var checkWatermark = false;
            if (decodedEntity != null)
            {
                checkWatermark = decodedEntity.CheckWatermark(WxOpenAppId);

                //保存用户信息（可选）
                if (checkWatermark && decodedEntity is DecodedUserInfo decodedUserInfo)
                {
                    var sessionBag = await SessionContainer.GetSessionAsync(sessionId);
                    if (sessionBag != null)
                    {
                        await SessionContainer.AddDecodedUserInfoAsync(sessionBag, decodedUserInfo);
                    }
                }
            }


            //注意：此处仅为演示，敏感信息请勿传递到客户端！
            return Json(new
            {
                success = checkWatermark,
                //decodedEntity = decodedEntity,
                msg = string.Format("水印验证：{0}",
                        checkWatermark ? "通过" : "不通过")
            });
        }

        
    }
}
