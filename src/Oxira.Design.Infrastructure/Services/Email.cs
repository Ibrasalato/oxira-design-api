using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Oxira.Design.Application.Common;

namespace Oxira.Design.Infrastructure.Services;

/// <summary>"Smtp" section. Zoho Mail: smtp.zoho.com, port 465, SSL; the password is an app password kept outside the repo.</summary>
public class SmtpOptions
{
    public string Host { get; set; } = "smtp.zoho.com";
    public int Port { get; set; } = 465;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromName { get; set; } = "Oxira.Design";
    public string FromAddress { get; set; } = "info@oxira.sa";
    /// <summary>off in development and tests: messages are only logged</summary>
    public bool Enabled { get; set; }
}

/// <summary>Requests never wait for SMTP: emails go into a channel and a background service sends them, with retries.</summary>
public class EmailQueue : IEmailSender
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
    public ChannelReader<EmailMessage> Reader => _channel.Reader;
    public void Enqueue(EmailMessage message) => _channel.Writer.TryWrite(message);
}

public class EmailWorker(EmailQueue queue, IOptions<SmtpOptions> opt, ILogger<EmailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var m in queue.Reader.ReadAllAsync(stop))
        {
            if (!opt.Value.Enabled) { log.LogInformation("Email (not sent, SMTP disabled) to {To}: {Subject}", string.Join(",", m.To), m.Subject); continue; }
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try { await SendAsync(m, stop); break; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "Email to {To} failed (attempt {Attempt})", string.Join(",", m.To), attempt);
                    await Task.Delay(TimeSpan.FromSeconds(10 * attempt), stop);
                }
            }
        }
    }

    private async Task SendAsync(EmailMessage m, CancellationToken ct)
    {
        var o = opt.Value;
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        foreach (var to in m.To) msg.To.Add(MailboxAddress.Parse(to));
        foreach (var bcc in m.Bcc ?? []) msg.Bcc.Add(MailboxAddress.Parse(bcc));
        if (m.ReplyTo != null) msg.ReplyTo.Add(MailboxAddress.Parse(m.ReplyTo));
        msg.Subject = m.Subject;
        var body = new BodyBuilder { HtmlBody = m.Html };
        foreach (var a in m.Attachments ?? [])
        {
            var type = ContentType.Parse(a.Mime);
            if (a.ContentId != null)
            {
                var res = body.LinkedResources.Add(a.Name, a.Content, type);
                res.ContentId = a.ContentId;
            }
            else body.Attachments.Add(a.Name, a.Content, type);
        }
        msg.Body = body.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(o.Host, o.Port, o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
        if (o.User != "") await smtp.AuthenticateAsync(o.User, o.Password, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
