"""Owner, 18 November 2026: customer emails from the helpdesk carry no Zammad branding or Zammad login link."""
import pathlib
import re
import shutil
import subprocess
import unittest

PROVISION = pathlib.Path(__file__).resolve().parents[2] / 'containers/zammad/provision.rb'
STOCK = ('auto reply (on new tickets)', 'auto reply (on follow-up of tickets)', 'customer notification (on owner change)')


class CustomerEmailBranding(unittest.TestCase):
    def setUp(self):
        text = PROVISION.read_text(encoding='utf-8')
        start = text.index('# Owner, 18 November 2026: emails to customers')
        self.block = text[start:text.index('# Webhook/trigger stay inactive', start)]
        self.pairs = {name: (seeded, swoovr) for name, seeded, swoovr in re.findall(r"'([^']+)' => \[\s*'([^']*)',\s*'([^']*)'\]", self.block)}

    def test_every_stock_customer_email_is_covered(self):
        self.assertEqual(set(self.pairs), set(STOCK))

    def test_seeded_text_is_zammads_and_swoovr_text_is_clean(self):
        for name, (seeded, swoovr) in self.pairs.items():
            with self.subTest(name):
                self.assertIn('your customer support system', seeded)
                self.assertNotRegex(swoovr, r'(?i)zammad|customer support system|product_name|config\.fqdn|ticket/zoom|password')
                self.assertIn('Swoovr Support', swoovr)

    def test_only_exact_stock_text_is_replaced_and_custom_text_keeps_its_words(self):
        # Codex review on PR 364: an edited message keeps its wording and loses only the Zammad footer.
        self.assertIn("current.strip == seeded.strip ? swoovr : current.sub(ZAMMAD_FOOTER, '')", self.block)
        if not shutil.which('ruby'):
            self.skipTest('ruby not installed')
        footer = re.search(r'ZAMMAD_FOOTER = (%r\{.*\})\n', self.block).group(1)
        seeded = self.pairs[STOCK[0]][0]
        script = f'''re = {footer}
custom = "<div>Our own wording</div>\\n<br/>\\n<div><i><a href=\\"https://zammad.com\\">Zammad</a>, your customer support system</i></div>"
print custom.sub(re, "")'''
        out = subprocess.run(['ruby', '-e', script], capture_output=True, text=True, check=True).stdout
        self.assertEqual(out, '<div>Our own wording</div>\n<br/>')
        stripped = subprocess.run(['ruby', '-e', f're = {footer}\nprint STDIN.read.sub(re, "")'], input=seeded, capture_output=True, text=True, check=True).stdout
        self.assertNotIn('customer support system', stripped)


if __name__ == '__main__':
    unittest.main()
