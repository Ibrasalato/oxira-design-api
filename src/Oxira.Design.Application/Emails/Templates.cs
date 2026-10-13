using System.Globalization;
using Oxira.Design.Application.Common;
using Oxira.Design.Domain;
using static Oxira.Design.Domain.Text;

namespace Oxira.Design.Application.Emails;

/// <summary>Oxira Design email layout and messages (ported from the n8n workflows).</summary>
public static partial class Templates
{
    public const string WhatsApp = "+966 59 669 4021";

    /// <summary>the client-facing layout: logo line, body, signature line</summary>
    public static string Shell(bool rtl, string inner, int width = 620) =>
        $"<div dir=\"{(rtl ? "rtl" : "ltr")}\" style=\"font-family:Tahoma,Arial,sans-serif;font-size:15px;line-height:1.8;color:#0A253E;max-width:{width}px;text-align:{(rtl ? "right" : "left")}\">" +
        "<p style=\"font-size:20px;font-weight:bold;margin:0 0 6px\" dir=\"ltr\">Oxira <span style=\"color:#007DB4\">Design</span></p>" +
        inner + "</div>";

    /// <summary>the team layout: Arabic, right to left</summary>
    public static string TeamShell(string title, string inner) =>
        "<div dir=\"rtl\" style=\"font-family:Tahoma,Arial,sans-serif;font-size:15px;line-height:1.7;color:#0A253E;text-align:right\">" +
        $"<h2 style=\"margin:0 0 12px\">{Html(title)}</h2>{inner}</div>";

    public static string Table(IEnumerable<(string Key, string? Value)> rows) =>
        "<table style=\"border-collapse:collapse;width:100%;max-width:720px\">" +
        string.Concat(rows.Select(r =>
            "<tr><th style=\"text-align:right;padding:9px 12px;background:#F4F6F8;border:1px solid #DCE3EA;width:170px;vertical-align:top\">" + Html(r.Key) + "</th>" +
            "<td style=\"padding:9px 12px;border:1px solid #DCE3EA\">" + (string.IsNullOrWhiteSpace(r.Value) ? "-" : Html(r.Value)) + "</td></tr>")) + "</table>";

    public static string P(string text, string style = "") => $"<p style=\"{style}\">{Html(text)}</p>";

    public static string Button(string href, string label) =>
        $"<p><a href=\"{Html(href)}\" style=\"display:inline-block;background:#F5A800;color:#0A253E;font-weight:700;padding:11px 20px;border-radius:10px;text-decoration:none\">{Html(label)}</a></p>";

    public static string Quote(string text) => $"<blockquote style=\"margin:0;padding:10px 14px;background:#F5F7F9;border-radius:10px\">{Html(text)}</blockquote>";

    public static string RiyadhTime(DateTimeOffset t) => t.ToOffset(Riyadh.Offset).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Money(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static EmailMessage Team(string team, string subject, string title, string inner, string? replyTo = null, IReadOnlyList<EmailAttachment>? files = null) =>
        new([team], subject, TeamShell(title, inner), replyTo, files);

    // ───── chat lead ─────
    public static EmailMessage TeamChatLead(Lead l, string team)
    {
        var rows = new (string, string?)[]
        {
            ("رقم الطلب", "#" + l.Id), ("الاسم", l.ContactName), ("الجوال", l.Phone), ("البريد", l.Email), ("المدينة", l.City),
            ("الباقة", l.Package), ("المساحة", l.Area), ("الاحتياج", l.Needs), ("طريقة التواصل", l.PreferredContact),
            ("مخطط الاستوديو", l.Context), ("لغة الصفحة", l.Lang), ("الصفحة", l.Page), ("الوقت", RiyadhTime(l.CreatedAt)),
        };
        return Team(team, $"طلب جديد من محادثة Oxira Design رقم {l.Id}: {l.ContactName}", "طلب جديد من المحادثة الذكية", Table(rows),
            IsEmail(l.Email) ? l.Email.Trim() : null);
    }

    // ───── orders ─────
    public static EmailMessage TeamOrder(Order o, string team, IReadOnlyList<EmailAttachment> files)
    {
        var rows = new (string, string?)[]
        {
            ("رقم الطلب", "#" + o.Id), ("الباقة", o.Package), ("السعر", o.PriceNote), ("الاسم", o.ContactName), ("الجوال", o.Phone), ("البريد", o.Email),
            ("المدينة", o.City), ("المساحة", o.Area > 0 ? $"{o.Area:0.#} م²" : ""), ("الستايل", o.Style), ("الغرف من الاستوديو", o.Rooms),
            ("الملاحظات", o.Notes), ("الملفات المرفقة", o.FileNames), ("لغة الصفحة", o.Lang), ("الوقت", RiyadhTime(o.CreatedAt)),
        };
        var inner = Table(rows) + P("الملفات مرفقة بهذا الإيميل، والتفاصيل الكاملة (ومنها المساحات والكميات) في جدول Orders في قاعدة بيانات Oxira Design.", "color:#556779;font-size:13px");
        return Team(team, $"طلب تصميم جديد #{o.Id} ({Packages.ArabicName(o.Package)}): {o.ContactName}", "طلب جديد من Oxira Design", inner,
            IsEmail(o.Email) ? o.Email : null, files);
    }

    private record OrderText(string Subject, string Hi, string Body, string Pay, string Sign);

    public static EmailMessage? ClientOrder(Order o)
    {
        if (!IsEmail(o.Email)) return null;
        var lang = Langs.Email.Contains(o.Lang) ? o.Lang : "en";
        var n = o.ContactName;
        var t = lang switch
        {
            "ar" => new OrderText($"استلمنا طلبك #{o.Id} | Oxira Design", $"أهلاً {n}،", $"شكراً لاختيارك Oxira Design. استلمنا طلبك رقم #{o.Id}، وسيتواصل معك فريقنا خلال يوم عمل.", "إذا لم تكمل الدفع بعد، تقدر ترجع لنموذج الطلب وتكمل الدفع.", "فريق أوكسيرا"),
            "de" => new OrderText($"Ihre Bestellung #{o.Id} ist eingegangen | Oxira Design", $"Hallo {n},", $"vielen Dank für Ihre Bestellung bei Oxira Design. Wir haben Ihre Bestellung #{o.Id} erhalten und melden uns innerhalb eines Arbeitstags.", "Falls die Zahlung noch offen ist, können Sie sie über das Bestellformular abschließen.", "Ihr Oxira-Team"),
            "fr" => new OrderText($"Commande #{o.Id} reçue | Oxira Design", $"Bonjour {n},", $"Merci d’avoir choisi Oxira Design. Nous avons reçu votre commande #{o.Id} et notre équipe vous contacte sous un jour ouvré.", "Si le paiement n’est pas terminé, vous pouvez le reprendre depuis le formulaire.", "L’équipe Oxira"),
            "ru" => new OrderText($"Заказ #{o.Id} получен | Oxira Design", $"Здравствуйте, {n}!", $"Спасибо, что выбрали Oxira Design. Мы получили заказ #{o.Id}, команда свяжется с вами в течение рабочего дня.", "Если оплата ещё не завершена, вернитесь к форме заказа.", "Команда Oxira"),
            _ => new OrderText($"We received your order #{o.Id} | Oxira Design", $"Hello {n},", $"Thank you for choosing Oxira Design. We received your order #{o.Id} and our team will contact you within one working day.", "If you have not completed the payment yet, you can return to the order form and pay.", "The Oxira team"),
        };
        var inner = P(t.Hi) + P(t.Body) + (o.Package == Packages.Quick ? P(t.Pay, "color:#556779") : "") +
            $"<p>{Html(t.Sign)}<br><a href=\"https://design.oxira.sa\">design.oxira.sa</a> · WhatsApp {WhatsApp}</p>";
        return new EmailMessage([o.Email], t.Subject, Shell(lang == "ar", inner), "info@oxira.sa");
    }

    public static IEnumerable<EmailMessage> OrderPaid(Order o, Invoice inv, string team)
    {
        var amount = Money(inv.Amount / 100m);
        yield return Team(team, $"تم الدفع ✅ طلب تصميم #{o.Id} ({Packages.ArabicName(o.Package)})", "تم دفع طلب تصميم",
            $"<p>رقم الطلب: <b>#{o.Id}</b><br>المبلغ: <b>{amount} ريال</b> شامل الضريبة<br>فاتورة ميسر: {Html(inv.Id)}<br>العميل: {Html(o.ContactName)} · {Html(o.Phone)}</p>" +
            P("ابدأ التنفيذ خلال 48 ساعة عمل. التفاصيل والملفات في إيميل الطلب."));
        if (!IsEmail(o.Email)) yield break;
        var ar = o.Lang == "ar";
        var body = ar
            ? $"شكراً لك! استلمنا دفعتك بقيمة {amount} ريال لطلب #{o.Id}. سيسلمك فريقنا الملفات خلال 48 ساعة عمل."
            : $"Thank you! We received your payment of {amount} SAR for order #{o.Id}. Our team will deliver your files within 48 working hours.";
        yield return new EmailMessage([o.Email], ar ? $"تم استلام الدفع لطلبك #{o.Id} | Oxira Design" : $"Payment received for order #{o.Id} | Oxira Design",
            Shell(ar, P(body) + $"<p>{(ar ? "فريق أوكسيرا" : "The Oxira team")}<br>WhatsApp {WhatsApp}</p>"), "info@oxira.sa");
    }
}
