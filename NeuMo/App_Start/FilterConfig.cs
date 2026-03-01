using System.Web;
using System.Web.Mvc;

namespace NeuMo
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new RequireHttpsAttribute()); // force HTTPS
            filters.Add(new HandleErrorAttribute());
            
        }
    }
}
