using CefSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CefClient.Common
{
    public static class CefSharpExtension
    {
        public static Task<string> GetCookieText(this IWebBrowser browser, string url)
        {
            var requestContext = browser.GetBrowserHost().RequestContext;
            var cookieManager = requestContext.GetCookieManager(null);
            CookieVisitor _cookieVisitor = new CookieVisitor();
            cookieManager.VisitUrlCookies(url, true, _cookieVisitor);
            return _cookieVisitor.Task;
        }
        public static Task<string> GetAllCookieText(this IWebBrowser browser)
        {
            var requestContext = browser.GetBrowserHost().RequestContext;
            var cookieManager = requestContext.GetCookieManager(null);
            CookieVisitor _cookieVisitor = new CookieVisitor();
            cookieManager.VisitAllCookies(_cookieVisitor);
            return _cookieVisitor.Task;
        }

    }
}
