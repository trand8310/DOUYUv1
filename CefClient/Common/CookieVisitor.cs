using CefSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CefClient.Common
{
    public class CookieVisitor : ICookieVisitor
    {
        private readonly TaskCompletionSource<string> taskCompletionSource;
        private List<Cookie> list;
        private bool DeleteCookie;
        public CookieVisitor(bool _deleteCookie = false)
        {
            this.DeleteCookie = _deleteCookie;
            taskCompletionSource = new TaskCompletionSource<string>();
            list = new List<Cookie>();
        }
        bool ICookieVisitor.Visit(Cookie cookie, int count, int total, ref bool deleteCookie)
        {
            if (DeleteCookie)
            {
                deleteCookie = DeleteCookie;
            }
            list.Add(cookie);
            if (count == (total - 1))
            {
                StringBuilder bufs = new StringBuilder();
                foreach (var item in list)
                {
                    bufs.AppendFormat("{0}={1};", item.Name, item.Value);
                }
                taskCompletionSource.TrySetResult(bufs.ToString());
            }
            return true;
        }
        void IDisposable.Dispose()
        {
            if (list != null && list.Count == 0)
            {
                taskCompletionSource.TrySetResult(null);
            }
            list = null;
        }
        public Task<string> Task
        {
            get { return taskCompletionSource.Task; }
        }

    }
}
