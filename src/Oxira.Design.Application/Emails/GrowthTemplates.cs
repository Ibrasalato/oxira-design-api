using System.Globalization;
using System.Text.Json.Nodes;
using Oxira.Design.Application.Common;
using Oxira.Design.Application.Features.Costs;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Emails;

public static partial class Templates
{
    // ───── listing tours ─────
    /// <summary>the owner gets the live link and a QR code; the team gets a short note</summary>
    public static IEnumerable<EmailMessage> ListingLive(Listing l, SiteOptions site)
    {
        var url = site.Site(Langs.Prefix(l.Lang) + "/tour/?id=" + l.Id);
        var qr = "https://api.qrserver.com/v1/create-qr-code/?size=360x360&margin=12&data=" + Uri.EscapeDataString(url);
        yield return new EmailMessage([site.TeamEmail], $"Listing tour paid: {l.Title} ({l.Id})",
            $"<p>A 3D listing tour was paid ({Prices.Listing:0} SAR) and is live.</p><p><a href=\"{url}\">{url}</a></p><p>Owner: {Html(l.Phone)} {Html(l.Email)}</p>");
        if (!IsEmail(l.Email)) yield break;
        var ar = l.Lang == "ar";
        var inner = $"<h2>{(ar ? "جولة الإعلان ثلاثية الأبعاد صارت منشورة" : "Your 3D listing tour is live")}</h2>" +
            P(ar ? "شارك هذا الرابط في إعلانك على عقار أو بيوت أو السوشيال ميديا:" : "Share this link in your listing on property portals and social media:") +
            $"<p><a href=\"{url}\">{url}</a></p>" + P(ar ? "رمز QR للمطبوعات واللوحات:" : "QR code for flyers and signboards:") +
            $"<img src=\"{qr}\" width=\"180\" height=\"180\" alt=\"QR\"><p style=\"color:#556779\">Oxira Design · design.oxira.sa</p>";
        yield return new EmailMessage([l.Email], ar ? "جولتك ثلاثية الأبعاد جاهزة: " + l.Title : "Your 3D tour is live: " + l.Title, Shell(ar, inner), "info@oxira.sa");
    }

    // ───── build-cost rates ─────
    public static EmailMessage CostProposal(CostRate row, List<CostHandlers.Change> changed, List<string> skipped, List<JsonObject> sources, string approve, string team)
    {
        string Fmt(long[] r) => string.Join(" – ", r.Select(n => n.ToString("N0", CultureInfo.InvariantCulture)));
        const string th = "padding:6px 10px;background:#F4F6F8;border:1px solid #DCE3EA";
        const string td = "padding:6px 10px;border:1px solid #DCE3EA";
        var inner = P(row.Notes == "" ? "لم يرجع البحث ملاحظات." : row.Notes) +
            (changed.Count > 0
                ? $"<table style=\"border-collapse:collapse;width:100%\"><tr><th style=\"text-align:right;{th}\">البند</th><th style=\"{th}\">الحالي</th><th style=\"{th}\">المقترح</th></tr>" +
                  string.Concat(changed.Select(c => $"<tr><td style=\"{td}\">{Html(c.Label)}</td><td dir=\"ltr\" style=\"{td}\">{Fmt(c.From)}</td><td dir=\"ltr\" style=\"{td};font-weight:700\">{Fmt(c.To)}</td></tr>")) + "</table>"
                : P("لا يوجد تغيير مقترح هذا الشهر.")) +
            (skipped.Count > 0 ? P("لم تُؤخذ: " + string.Join("، ", skipped), "color:#8A5A00") : "") +
            (sources.Count > 0 ? "<p><b>المصادر:</b><br>" + string.Join("<br>", sources.Select(s => $"<a href=\"{Html(s["url"]?.ToString())}\">{Html(s["title"]?.ToString() is { Length: > 0 } t ? t : s["url"]?.ToString())}</a>")) + "</p>" : "") +
            (changed.Count > 0 ? Button(approve, "اعتمد الأسعار الجديدة") + P("لن تتغير الأسعار في الموقع قبل الضغط على الرابط. لو تجاهلت الرسالة تبقى الأسعار الحالية.", "color:#556779;font-size:13px") : "");
        return new EmailMessage([team], changed.Count > 0 ? $"اعتماد أسعار البناء لشهر {row.Updated} ({changed.Count} تغيير)" : $"أسعار البناء لشهر {row.Updated}: لا تغيير",
            TeamShell("تحديث أسعار حاسبة تكلفة البناء", inner));
    }

    /// <summary>small HTML pages for links opened from emails (approval, unsubscribe)</summary>
    public static string Page(string title, string body, string link, string linkText, string lang = "ar") =>
        $"<!doctype html><html lang=\"{lang}\" dir=\"{(lang == "ar" ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Oxira Design</title></head>" +
        $"<body style=\"font-family:Tahoma,Arial,sans-serif;color:#0A253E;max-width:520px;margin:15vh auto;padding:0 20px;text-align:center\"><h2>{Html(title)}</h2><p>{body}</p><p><a href=\"{link}\">{Html(linkText)}</a></p></body></html>";

    public static string ApprovedPage(bool ok, SiteOptions site) => ok
        ? Page("تم اعتماد الأسعار الجديدة", "الحاسبة في الموقع تستخدمها الآن (قد يستغرق ظهورها حتى ساعة).", site.Site("/cost/"), "design.oxira.sa/cost/")
        : Page("الرابط غير صالح أو استُخدم من قبل", "لم يتغير شيء في الأسعار.", site.Site("/cost/"), "design.oxira.sa/cost/");

    public static string OptOutPage(bool ok, SiteOptions site) => ok
        ? Page("تم إيقاف الرسائل · You are unsubscribed", "لن تصلك رسائل المتابعة من Oxira Design. ستصلك فقط رسائل طلباتك.<br>You will not get follow-up emails from Oxira Design, only emails about your orders.", site.Site("/"), "design.oxira.sa")
        : Page("Invalid link", "Email info@oxira.sa to unsubscribe.", site.Site("/"), "design.oxira.sa", "en");

    // ───── follow-ups ─────
    private static string Brand(bool rtl, string body) =>
        $"<div dir=\"{(rtl ? "rtl" : "ltr")}\" style=\"font-family:Tahoma,Arial,sans-serif;font-size:15px;line-height:1.8;color:#0A253E;max-width:560px;text-align:{(rtl ? "right" : "left")}\">" +
        "<p style=\"font-size:20px;font-weight:bold;margin:0 0 8px\" dir=\"ltr\">Oxira <span style=\"color:#fff;background:#007DB4;padding:1px 6px;font-size:14px\">Design</span></p>" + body + "</div>";

    private static string Stop(string link, bool ar) =>
        $"<p style=\"color:#8796A5;font-size:12px;margin-top:22px\">{(ar ? "لا تريد هذه الرسائل؟ " : "Don't want these emails? ")}<a style=\"color:#8796A5\" href=\"{link}\">{(ar ? "إيقاف الرسائل" : "Unsubscribe")}</a></p>";

    public static EmailMessage PlanNudge(Project p, SiteOptions site, string stop)
    {
        var ar = p.Lang == "ar";
        var pre = ar ? "" : Langs.Prefix(p.Lang == "ar" ? "en" : p.Lang);
        var open = site.Site(pre + "/plan/?p=" + p.Pid);
        var body = ar
            ? $"<h2 style=\"margin:0 0 8px\">مخططك «{Html(p.Title)}» محفوظ</h2>" + P("إذا عجبك المخطط، فريقنا يقدر يحوّله لمخططات معتمدة وواجهات ورندرات واقعية، ويراجعه مهندس حسب اشتراطات بلديتك. نرسل لك عرض السعر خلال يوم عمل.") +
              Button(open + "#pl-order", "اطلب عرض سعر لمخططك") + $"<p>أو كمّل التعديل عليه: <a href=\"{open}\">افتح المخطط</a></p>"
            : $"<h2 style=\"margin:0 0 8px\">Your plan “{Html(p.Title)}” is saved</h2>" + P("If you like it, our team can turn it into approved drawings, facades and photoreal renders, reviewed by an engineer for your local rules. We send a quote within one working day.") +
              Button(open + "#pl-order", "Get a quote for your plan") + $"<p>Or keep editing: <a href=\"{open}\">open the plan</a></p>";
        return new EmailMessage([p.Owner], ar ? "مخططك جاهز للخطوة الجاية | Oxira Design" : "Your plan is ready for the next step | Oxira Design", Brand(ar, body + Stop(stop, ar)));
    }

    public static EmailMessage RatingRequest(Order o, SiteOptions site, string stop)
    {
        var ar = o.Lang == "ar";
        var link = site.Site((ar ? "" : Langs.Prefix(o.Lang)) + "/account/?tab=orders");
        var body = ar
            ? "<h2 style=\"margin:0 0 8px\">كيف كان تصميمك؟</h2>" + P($"سلّمنا طلبك #{o.Id} قبل أيام. تقييمك يساعد العملاء الجدد يختارون، ويساعد المصمم. يأخذ دقيقة.") + Button(link, "قيّم الطلب")
            : "<h2 style=\"margin:0 0 8px\">How was your design?</h2>" + P($"We delivered order #{o.Id} a few days ago. Your rating helps new clients choose and helps your designer. It takes a minute.") + Button(link, "Rate your order");
        return new EmailMessage([o.Email], ar ? $"قيّم طلبك #{o.Id} | Oxira Design" : $"Rate your order #{o.Id} | Oxira Design", Brand(ar, body + Stop(stop, ar)));
    }

    public static EmailMessage DesignerAlert(Order o, SiteOptions site, IReadOnlyList<string> designers)
    {
        var body = $"<h2 style=\"margin:0 0 8px\">طلب جديد #{o.Id} · New order #{o.Id}</h2>" +
            P($"{o.Package} · {o.City}{(o.Area > 0 ? $" · {o.Area.ToString("0.#", CultureInfo.InvariantCulture)} m²" : "")} · {o.Lang}") +
            P("أول مصمم يستلمه من البوابة يكون له. The first designer to take it in the portal gets it.") +
            Button(site.Site("/account/?tab=designer"), "افتح البوابة · Open the portal");
        return new EmailMessage([site.TeamEmail], $"طلب جديد متاح #{o.Id} | New order available | Oxira Design", Brand(true, body), null, null, designers);
    }

    // ───── weekly report ─────
    public record WeekNumbers(int PlansWeek, int PlansAll, int Published, int OrdersWeek, int OrdersAll, int DeliveredWeek, int Open,
        double Rating, int Ratings, int DesignersActive, int DesignersPending, int WaitWeek, int WaitAll);

    public static EmailMessage WeeklyReport(WeekNumbers n, string slug, string desc, IReadOnlyDictionary<string, string> cap, string team)
    {
        static string Row(string k, object v) => $"<tr><td style=\"padding:6px 10px;border-bottom:1px solid #E3E8EE\">{Html(k)}</td><td style=\"padding:6px 10px;border-bottom:1px solid #E3E8EE;font-weight:700\">{Html(Convert.ToString(v, CultureInfo.InvariantCulture))}</td></tr>";
        static string Block(string t, string? v) => string.IsNullOrWhiteSpace(v) ? "" : $"<h3 style=\"margin:18px 0 6px\">{Html(t)}</h3><div style=\"background:#F5F7F9;border-radius:10px;padding:10px 12px\">{Html(v)}</div>";
        var html = "<div style=\"font-family:Tahoma,Arial,sans-serif;font-size:14px;line-height:1.7;color:#0A253E;max-width:640px\" dir=\"rtl\"><h2 style=\"margin:0 0 10px\">Oxira Design · الأسبوع بالأرقام</h2><table style=\"border-collapse:collapse;width:100%\">" +
            Row("مخططات محفوظة هذا الأسبوع / الكل", $"{n.PlansWeek} / {n.PlansAll}") + Row("منشورة في المعرض", n.Published) +
            Row("طلبات هذا الأسبوع / الكل", $"{n.OrdersWeek} / {n.OrdersAll}") + Row("تم تسليمها هذا الأسبوع", n.DeliveredWeek) + Row("طلبات بدون مصمم", n.Open) +
            Row("متوسط التقييم", n.Ratings > 0 ? $"{n.Rating.ToString("0.0", CultureInfo.InvariantCulture)} ({n.Ratings})" : "-") +
            Row("مصممون نشطون / بانتظار الموافقة", $"{n.DesignersActive} / {n.DesignersPending}") + Row("قائمة انتظار Pro هذا الأسبوع / الكل", $"{n.WaitWeek} / {n.WaitAll}") +
            $"</table><h2 style=\"margin:24px 0 6px\">مخطط الأسبوع للسوشيال</h2><p dir=\"ltr\">{Html(desc)}<br><a href=\"https://design.oxira.sa/plans/{slug}/\">design.oxira.sa/plans/{slug}/</a></p>" +
            Block("Instagram (عربي)", cap.GetValueOrDefault("instagram_ar")) + Block("Instagram (English)", cap.GetValueOrDefault("instagram_en")) +
            Block("Pinterest", string.Join("\n", new[] { cap.GetValueOrDefault("pinterest_title_en"), cap.GetValueOrDefault("pinterest_desc_en") }.Where(x => !string.IsNullOrEmpty(x)))) +
            Block("TikTok · أول ثانيتين", cap.GetValueOrDefault("tiktok_hook_ar")) + Block("X", cap.GetValueOrDefault("x_en")) +
            "<p style=\"color:#556779;font-size:12px;margin-top:18px\">صوّر الشاشة وأنت تفتح المخطط وتحرّك جدار وتشوفه 3D، 15 ثانية تكفي. هذا التقرير يوصلك كل أحد من Oxira Design API.</p></div>";
        return new EmailMessage([team], "Oxira Design · الأسبوع بالأرقام + مخطط الأسبوع", html);
    }
}
