from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .duplicate_policy import DuplicatePolicy
    from .ocr_mode import OcrMode

@dataclass
class LibrarySettingsRequest(AdditionalDataHolder, Parsable):
    """
    Library settings; omitted values keep their current value.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The autoProcess property
    auto_process: Optional[bool] = None
    # The duplicatePolicy property
    duplicate_policy: Optional[DuplicatePolicy] = None
    # The ocrLanguages property
    ocr_languages: Optional[str] = None
    # The ocrMode property
    ocr_mode: Optional[OcrMode] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> LibrarySettingsRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: LibrarySettingsRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return LibrarySettingsRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .duplicate_policy import DuplicatePolicy
        from .ocr_mode import OcrMode

        from .duplicate_policy import DuplicatePolicy
        from .ocr_mode import OcrMode

        fields: dict[str, Callable[[Any], None]] = {
            "autoProcess": lambda n : setattr(self, 'auto_process', n.get_bool_value()),
            "duplicatePolicy": lambda n : setattr(self, 'duplicate_policy', n.get_enum_value(DuplicatePolicy)),
            "ocrLanguages": lambda n : setattr(self, 'ocr_languages', n.get_str_value()),
            "ocrMode": lambda n : setattr(self, 'ocr_mode', n.get_enum_value(OcrMode)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("autoProcess", self.auto_process)
        writer.write_enum_value("duplicatePolicy", self.duplicate_policy)
        writer.write_str_value("ocrLanguages", self.ocr_languages)
        writer.write_enum_value("ocrMode", self.ocr_mode)
        writer.write_additional_data_value(self.additional_data)
    

