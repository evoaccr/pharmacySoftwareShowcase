namespace pharmacyApp.Modules.Medula;

public static class MedulaWarningBridge
{
    public const bool SuppressNativeScriptDialogs = true;

    public const string Script = """
        (() => {
            const prefix = '__PHARMACY_MEDULA_WARNING__';
            const relayType = '__PHARMACY_MEDULA_MESSAGE_RELAY__';
            const fallbackPattern = /(?:(?:^|[^a-zçğıöşü])hata(?:s[ıi])?(?:$|[^a-zçğıöşü])|uyar[ıi]|zorunlu|olmal[ıi]d[ıi]r|girilmelidir|giriniz|se[cç]iniz|bulunamad[ıi]|ge[cç]ersiz|ba[sş]ar[ıi]s[ıi]z|uygun de[gğ]ildir|izin verilenler aras[ıi]nda yer almamaktad[ıi]r|[cç]al[ıi][sş](?:mamaktad[ıi]r|m[ıi]yor|maz)|daha [oö]nce|kar[sş][ıi]lan|kaydedil|silin|onaylan|tamamlan|d[uü][sş][uü]r[uü]ld[uü]|d[uü]zenlendi|g[uü]ncellendi|de[gğ]i[sş]tirildi)/i;
            const informationalPattern = /(?:hatal[ıi]\s+bilgileri\s+d[uü]zelttirmek\s+i[cç]in\s+sgk\s+sosyal\s+g[uü]venlik\s+merkezlerine\s+ba[sş]vurunuz|bilgi\s+g[uü]venli[gğ]i\s+nedeniyle|kay[ıi]t\s+alt[ıi]na\s+al[ıi]nmaktad[ıi]r)/i;
            const warningSelector = [
                '[role="alert"]', '[role="status"]', '[aria-live]',
                '.error', '.errors', '.errorMessage', '.message-error',
                '.message', '.messages', '[class*="message"]', '[id*="message"]',
                '[class*="warning"]', '[class*="uyari"]', '[class*="uyarı"]',
                '[class*="hata"]', '[class*="success"]', '[class*="approved"]',
                '[class*="notice"]', '[class*="info"]', '[id*="warning"]',
                '[id*="uyari"]', '[id*="uyarı"]', '[id*="hata"]',
                '[id*="success"]', '[id*="message"]', 'font[color]', '[style*="color"]',
                '[style*="background"]'
            ].join(',');
            const rgb = value => {
                const match = String(value || '').match(/rgba?\(\s*(\d+)\D+(\d+)\D+(\d+)/i);
                return match ? [Number(match[1]), Number(match[2]), Number(match[3])] : null;
            };
            const colourSeverity = element => {
                const computed = getComputedStyle(element);
                for (const colour of [rgb(computed.color), rgb(computed.backgroundColor)]) {
                    if (!colour) continue;
                    const [red, green, blue] = colour;
                    if (red >= 120 && red > green * 1.28 && red > blue * 1.28) return 'error';
                    if (green >= 85 && green > red * 1.12 && green > blue * 1.18) return 'success';
                    if (red >= 110 && green >= 75 && blue < Math.min(red, green) * 0.78) return 'notice';
                }
                return '';
            };
            const semanticSeverity = element => {
                const marker = `${element.id || ''} ${element.className || ''}`.toLowerCase();
                if (/error|danger|hata/.test(marker)) return 'error';
                if (/success|approved|confirm|basari|başarı/.test(marker)) return 'success';
                if (/warning|notice|info|uyari|uyarı/.test(marker)) return 'notice';
                return '';
            };
            const textSeverity = text => /(?:başarıyla|başarı\s+ile|başarılı\s+olarak|kaydedil(?:di|miştir)|silin(?:di|miştir)|onaylan(?:dı|mıştır)|işlem\s+tamamlan(?:dı|mıştır)|kayıt\s+tamamlandı)/i.test(text)
                ? 'success'
                : /(?:d[uü][sş][uü]r[uü]ld[uü]|d[uü]zenlendi|maksimum\s+ila[cç]\s+adet|otomatik|g[uü]ncellendi|de[gğ]i[sş]tirildi|bilgilendirme|karekod\s+bilgisi\s+tekrar\s+okutulmu[sş])/i.test(text)
                    ? 'notice'
                    : fallbackPattern.test(text) ? 'error' : '';
            const postToHost = (severity, text) => {
                try { chrome.webview.postMessage(prefix + severity + '\u001f' + text); } catch { }
            };
            const isolateMessage = value => {
                const text = String(value ?? '').replace(/\s+/g, ' ').trim();
                const documentValidation = text.match(
                    /(Kaydetmeden önce doküman türünü seçiniz\.?|Gönderilen dosyanın uzantısı izin verilenler arasında yer almamaktadır\.\s*\(Eklenebilecek dosya uzantıları:\s*pdf,\s*jpg,\s*jpeg,\s*doc,\s*docx,\s*xls,\s*xlsx\))/i);
                if (documentValidation) return documentValidation[1];
                const repeatedQr = text.match(
                    /(?:^|\s)([0-9A-Z]{20,120}\s+Karekod\s+bilgisi\s+tekrar\s+okutulmu[sş]\.?)\b/i);
                if (repeatedQr) return repeatedQr[1];
                const quantityAdjustment = text.match(
                    /([^.;]{1,220};\s*[İI]la[cç]\s+adedi,?\s+ilgili\s+SUT\s+kural[ıi]nda\s+yer\s+alan\s+maksimum\s+ila[cç]\s+adet\s+bilgisine\s+g[oö]re\s+d[uü]zenlendi\.?)/i);
                return quantityAdjustment?.[1] || text;
            };
            const send = (message, severity = '') => {
                const text = isolateMessage(message);
                if (!text.trim()) return;
                severity ||= textSeverity(text);
                if (window === window.top) {
                    postToHost(severity, text);
                    return;
                }
                try { window.top.postMessage({ type: relayType, severity, text }, '*'); }
                catch { postToHost(severity, text); }
            };
            const visible = element => !!element?.getClientRects?.().length
                && getComputedStyle(element).visibility !== 'hidden'
                && getComputedStyle(element).display !== 'none';
            const inspectElement = element => {
                if (!element || element.nodeType !== Node.ELEMENT_NODE) return;
                const candidates = [element];
                if (element.querySelectorAll)
                    candidates.push(...element.querySelectorAll(warningSelector));
                const matches = [];
                for (const candidate of candidates) {
                    if (!visible(candidate)) continue;
                    if (candidate.matches?.('html,body,form,input,select,textarea,button,a,label')
                        || candidate.querySelectorAll?.('input,select,textarea,button,a').length > 4)
                        continue;
                    const containingDataTable = candidate.closest?.(
                        'table[id*="tableEx"],table[id*="TableEx"],table[id*="tbl"]');
                    if (containingDataTable && candidate.getAttribute?.('role') !== 'alert') continue;
                    const text = String(candidate.innerText || candidate.textContent || '').trim();
                    if (/^silinmiş\s+karekod$/i.test(text)) continue;
                    if (informationalPattern.test(text)) continue;
                    if (/^Branş Seçiniz\b/i.test(text)
                        && candidate.querySelector?.('[id^="f:tableExDoktorBransList"],#f\\:tableExBransList'))
                        continue;
                    const textBasedSeverity = textSeverity(text);
                    const semantic = semanticSeverity(candidate);
                    const colour = colourSeverity(candidate);
                    const role = candidate.getAttribute?.('role');
                    const roleSeverity = role === 'alert'
                        ? colour || semantic || 'error'
                        : role === 'status' || candidate.hasAttribute?.('aria-live')
                            ? colour || semantic || 'notice'
                            : '';
                    const severity = textBasedSeverity || roleSeverity || semantic || colour;
                    const isMessageCandidate = candidate.matches?.(warningSelector) === true;
                    if (text && text.length <= 1000
                        && (severity && isMessageCandidate || fallbackPattern.test(text))) {
                        matches.push({
                            text,
                            severity: severity || textSeverity(text),
                            priority: role === 'alert' ? 4
                                : role === 'status' || candidate.hasAttribute?.('aria-live') ? 3
                                : semanticSeverity(candidate) ? 2
                                : colourSeverity(candidate) ? 1 : 0
                        });
                    }
                }
                const best = matches.sort((left, right) =>
                    right.priority - left.priority || left.text.length - right.text.length)[0];
                if (best) send(best.text, best.severity);
            };
            const install = () => {
                if (window.__pharmacyMedulaWarningBridgeInstalled) return;
                window.__pharmacyMedulaWarningBridgeInstalled = true;
                if (window === window.top) {
                    addEventListener('message', event => {
                        if (event.data?.type === relayType && typeof event.data.text === 'string')
                            postToHost(
                                event.data.severity || textSeverity(event.data.text),
                                isolateMessage(event.data.text));
                    });
                }
                window.alert = message => {
                    send(message);
                };
                const observer = new MutationObserver(records => {
                    for (const record of records) {
                        if (record.type === 'characterData')
                            inspectElement(record.target.parentElement);
                        else {
                            inspectElement(record.target);
                            record.addedNodes.forEach(node => inspectElement(
                                node.nodeType === Node.ELEMENT_NODE ? node : node.parentElement));
                        }
                    }
                });
                observer.observe(document.documentElement, {
                    subtree: true,
                    childList: true,
                    characterData: true,
                    attributes: true,
                    attributeFilter: ['class', 'style', 'hidden']
                });
                window.__pharmacyMedulaInspectMessages = () => inspectElement(document.body);
                if (document.body) inspectElement(document.body);
            };
            if (document.documentElement) install();
            else addEventListener('DOMContentLoaded', install, { once: true });
        })();
        """;
}
