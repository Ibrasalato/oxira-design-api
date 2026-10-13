using Oxira.Design.Application.Common;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Emails;

public static partial class Templates
{
    /// <summary>a note to the team, and a receipt with the restore link to the buyer (it opens the balance on any device)</summary>
    public static IEnumerable<EmailMessage> PackBought(Purchase p, int balance, SiteOptions site)
    {
        yield return new EmailMessage([site.TeamEmail], $"شراء ريندرات ✅ {p.Renders} ريندر - {p.Amount:0} ريال",
            TeamShell("شراء باقة ريندرات", $"<p>تم شراء باقة <b>{p.Renders}</b> ريندر بقيمة <b>{p.Amount:0} ريال</b> (طلب #{p.Id}).<br>المحفظة: {Html(p.WalletId)}<br>الرصيد الآن: {balance}<br>البريد: {Html(p.Email == "" ? "-" : p.Email)}</p>"));
        if (!IsEmail(p.Email)) yield break;
        var ar = p.Lang == "ar";
        var link = site.Site(Langs.Prefix(p.Lang) + "/redesign/?w=" + p.WalletId);
        var inner = P(ar ? $"شكراً لك! أضفنا {p.Renders} ريندر لرصيدك، ورصيدك الآن {balance}." : $"Thank you! We added {p.Renders} renders to your balance. You now have {balance}.") +
            Button(link, ar ? "صمّم غرفتك الآن" : "Redesign a room now") +
            P(ar ? "احتفظ بهذا الرابط، يفتح رصيدك على أي جهاز." : "Keep this link: it opens your balance on any device.", "color:#556779;font-size:13px");
        yield return new EmailMessage([p.Email], ar ? $"تم شحن رصيدك: {p.Renders} ريندر | Oxira Design" : $"Your {p.Renders} room renders are ready | Oxira Design",
            Shell(ar, inner), "info@oxira.sa");
    }
}
