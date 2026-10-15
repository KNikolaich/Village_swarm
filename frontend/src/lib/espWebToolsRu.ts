import { getLanguage } from '@/i18n'

/**
 * Russian texts of the ESP Web Tools flashing dialogs, keyed by the English original. The bundle is rewritten at
 * build time to look texts up here (vite-plugins/esp-web-tools-i18n.ts); "{0}" is the one argument (a name, a version).
 */
const ru: Record<string, string> = {
  // Dashboard
  'Connecting': 'Подключение',
  'Connected to {0}': 'Подключено: {0}',
  'Install {0}': 'Установить {0}',
  'Update {0}': 'Обновить {0}',
  'Visit Device': 'Открыть устройство',
  'Add to Home Assistant': 'Добавить в Home Assistant',
  'Connect to Wi-Fi': 'Подключить к Wi-Fi',
  'Change Wi-Fi': 'Сменить Wi-Fi',
  'Logs & Console': 'Журнал и консоль',
  'Fund Development': 'Поддержать разработку',
  'Erase User Data': 'Стереть данные пользователя',
  // Errors
  'Error': 'Ошибка',
  'Disconnected': 'Устройство отключено',
  'Close': 'Закрыть',
  'Serial port is not readable/writable. Close any other application using it and try again.':
    'Порт недоступен. Закройте другие программы, которые его используют, и попробуйте снова.',
  'Serial port is not ready. Close any other application using it and try again.':
    'Порт не готов. Закройте другие программы, которые его используют, и попробуйте снова.',
  'Failed to download manifest': 'Не удалось скачать описание прошивки',
  // Wi-Fi (Improv)
  'Configure Wi-Fi': 'Настройка Wi-Fi',
  'Scanning for networks': 'Поиск сетей',
  'Trying to connect': 'Подключение к сети',
  'Unable to connect': 'Не удалось подключиться',
  'Timeout': 'Время ожидания истекло',
  'Unknown error ({0})': 'Неизвестная ошибка ({0})',
  'Connect your device to the network to start using it.': 'Подключите устройство к сети, чтобы начать им пользоваться.',
  'Network': 'Сеть',
  'Join other…': 'Другая сеть…',
  'Network Name': 'Имя сети',
  'Password': 'Пароль',
  'Skip': 'Пропустить',
  'Back': 'Назад',
  'Connect': 'Подключить',
  'Device connected to the network!': 'Устройство подключено к сети!',
  'Continue': 'Продолжить',
  // Install
  'Erase device': 'Стереть устройство',
  'Do you want to erase the device before installing {0}? All data on the device will be lost.':
    'Стереть устройство перед установкой {0}? Все данные на устройстве будут потеряны.',
  'Next': 'Далее',
  'Do you want to reset your device and erase all user data from your device?':
    'Сбросить устройство и стереть с него все данные пользователя?',
  'Confirm Installation': 'Подтверждение установки',
  'Your device is running {0}.': 'На устройстве сейчас {0}.',
  'Do you want to install {0}?': 'Установить {0}?',
  'Do you want to update to {0}?': 'Обновить до {0}?',
  'All data on the device will be erased.': 'Все данные на устройстве будут стёрты.',
  'Install': 'Установить',
  'Installing': 'Установка',
  'Preparing installation': 'Подготовка к установке',
  'Erasing': 'Стирание',
  'Wrapping up': 'Завершение',
  'This will take a minute.': 'Это займёт около минуты.',
  'This will take 2 minutes.': 'Это займёт около 2 минут.',
  'Keep this page visible to prevent slow down': 'Не сворачивайте страницу, иначе прошивка замедлится',
  'Installation complete!': 'Установка завершена!',
  'Installation failed': 'Установка не удалась',
  'Failed to initialize. Try resetting your device or holding the BOOT button while clicking INSTALL.':
    'Не удалось связаться с платой. Нажмите на ней RESET или держите кнопку BOOT, пока нажимаете «Установить».',
  // Logs
  'Logs': 'Журнал',
  'Reset Device': 'Перезагрузить устройство',
  'Download Logs': 'Скачать журнал',
  // No port selected
  'No port selected': 'Порт не выбран',
  'Cancel': 'Отмена',
  'Try Again': 'Попробовать снова',
  "If you didn't select a port because you didn't see your device listed, try the following steps:":
    'Если вы не выбрали порт, потому что не нашли устройство в списке, попробуйте следующее:',
  'Make sure that the device is connected to this computer (the one that runs the browser that shows this website)':
    'Убедитесь, что устройство подключено к этому компьютеру (тому, где открыт браузер с этой страницей)',
  'Most devices have a tiny light when it is powered on. If yours has one, make sure it is on.':
    'У большинства плат есть светодиод питания. Если он есть и на вашей, убедитесь, что он горит.',
  'Make sure that the USB cable you use can be used for data and is not a power-only cable.':
    'Убедитесь, что USB-кабель передаёт данные, а не только питание.',
  'Make sure you have the right drivers installed. Below are the drivers for common chips used in ESP devices:':
    'Убедитесь, что установлены нужные драйверы. Ниже драйверы для распространённых USB-чипов на платах ESP:',
  'If you are using a Linux flavor, make sure that your user is part of the':
    'Если у вас Linux, убедитесь, что ваш пользователь входит в группу',
  'group so it has permission to access the device.':
    'и поэтому имеет доступ к устройству.',
  'You may need to log out & back in or reboot to activate the new group access.':
    'Чтобы группа заработала, может понадобиться выйти из системы и войти снова или перезагрузиться.',
  'CP2102 drivers:':
    'Драйверы CP2102:',
  'CH342, CH343, CH9102 drivers:':
    'Драйверы CH342, CH343, CH9102:',
  'CH340, CH341 drivers:':
    'Драйверы CH340, CH341:',
  '(download via blue button with': '(скачивается синей кнопкой со значком',
  'icon)': ')',
}

declare global {
  var __ewtDict: Record<string, string> | undefined
}

/** Call before the ESP Web Tools bundle opens a dialog; the language is fixed for the page's lifetime. */
export function translateEspWebTools() {
  globalThis.__ewtDict = getLanguage() === 'ru' ? ru : undefined
}
