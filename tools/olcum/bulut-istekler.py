"""Aynı beş görüşmeyi buluta gönderecek istekleri yazar."""
import io
import json
import os

OUT = os.path.dirname(os.path.abspath(__file__))
CALLS = (24, 14, 38, 16, 17)

settings = json.load(io.open(
    os.path.join(os.environ['LOCALAPPDATA'], 'SocialZeka.Data', 'settings.json'),
    encoding='utf-8-sig'))

ex5 = next(e for e in settings['SttEndpoints'] if e['Kind'] == 'ex5')

for call in CALLS:
    request = {
        "id": f"bulut-{call}",
        "engine": "cloud-ex5",
        "mic_path": os.path.join(OUT, f'call-{call}-mic.wav'),
        "far_path": os.path.join(OUT, f'call-{call}-far.wav'),
        "model_ref": f"{ex5['BaseUrl']}|{ex5['ApiKey']}|{ex5['Model']}",
        "language": "tr",
        "word_timestamps": True,
    }

    with open(os.path.join(OUT, f'bulut-istek-{call}.json'), 'w', encoding='utf-8') as f:
        json.dump(request, f)

print(len(CALLS), 'bulut istegi hazir:', ex5['BaseUrl'], ex5['Model'])
