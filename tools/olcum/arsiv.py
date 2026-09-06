"""Arşiv nerede — çatal sonrası iki yer olabilir.

SocialZeka kendi veri klasörünü kullanır (`%LOCALAPPDATA%\\SocialZeka.Data`), ama
arşiv ilk açılışta devralınana kadar hâlâ VoiceTranscript'in klasöründedir. Ölçüm
betikleri ikisini de bilmek zorunda: ölçülecek görüşmeler nerede duruyorsa oradadır.

Salt okunur açılır. Bu klasördeki hiçbir betik arşive yazmaz.
"""
import os

APPLICATION = 'SocialZeka.Data'
LEGACY = 'VoiceTranscript.Data'
FILE = 'voicetranscript.db'


def database_path():
    """Bulunan ilk arşiv dosyası; ikisi de yoksa None."""
    local = os.environ.get('LOCALAPPDATA', '')

    for folder in (APPLICATION, LEGACY):
        path = os.path.join(local, folder, FILE)
        if os.path.exists(path):
            return path

    return None


def open_read_only():
    """Salt okunur bağlantı, ya da anlaşılır bir mesajla None."""
    import sqlite3

    path = database_path()
    if path is None:
        print(f'Arşiv bulunamadı: %LOCALAPPDATA%\\{APPLICATION} ya da \\{LEGACY} altında {FILE} yok.')
        print('Uygulama bu makinede hiç açılmamış olabilir; ölçüm gerçek görüşmeler ister.')
        return None

    connection = sqlite3.connect(f'file:{path}?mode=ro', uri=True)
    connection.row_factory = sqlite3.Row

    print(f'Arşiv: {path}')
    return connection
